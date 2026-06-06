using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;

namespace IntelliTect.Dropbox
{
    /// <summary>
    /// Cursor-validated, write-through metadata cache for a single Dropbox
    /// account.
    ///
    /// Each cache entry holds the items returned by a prior non-recursive
    /// <c>list_folder</c> for a specific path together with the cursor that
    /// describes that snapshot. Subsequent reads call
    /// <c>list_folder/continue(cursor)</c>, apply deltas to the in-memory
    /// snapshot, and return — typically a single fast round-trip when nothing
    /// has changed.
    ///
    /// Entries are persisted to a single-file SQLite database placed directly
    /// under <c>%LOCALAPPDATA%\DbxProvider\cache\</c>. The file is named
    /// <c>DropboxCache.&lt;email&gt;.db</c> using the account's email (sanitized for
    /// the file system); when no email is available a SHA-256 hash of the
    /// account id is used instead (<c>DropboxCache.&lt;accountIdHash&gt;.db</c>).
    /// A per-email entry in <see cref="CacheOptions.EmailDatabasePathOverrides"/>
    /// can redirect a specific account's database to an explicit file path.
    /// The persistent store is <b>unbounded</b> — nothing is ever evicted from
    /// disk. Entries are hydrated lazily (per path, on demand) so startup never
    /// pays to read the whole store. A bounded in-memory working set keeps the
    /// hot paths resident; when it is exceeded the least-recently-used entries
    /// are flushed to the database and dropped from memory only — they are
    /// re-loaded transparently on the next access. Dropbox always remains the
    /// master: every served result is reconciled against the stored cursor.
    /// </summary>
    public sealed class MetadataCache : IDisposable
    {
        public sealed class Entry
        {
            public string Path { get; set; } = "";
            public string PathLower { get; set; } = "";
            public string Cursor { get; set; } = "";
            public List<DropboxItem> Items { get; set; } = new();
            public DateTime LastValidatedUtc { get; set; }
            public DateTime LastUsedUtc { get; set; }
            public bool Dirty { get; set; }
        }

        /// <summary>
        /// Lightweight, items-free projection of a cache entry for observability
        /// (e.g. <c>Get-DropboxCacheInfo</c>). Reading this from the database
        /// avoids deserializing every entry's full item list.
        /// </summary>
        public sealed class EntryInfo
        {
            public string Path { get; set; } = "";
            public int ItemCount { get; set; }
            public string Cursor { get; set; } = "";
            public DateTime LastValidatedUtc { get; set; }
            public DateTime LastUsedUtc { get; set; }
            public bool Dirty { get; set; }
            public bool InMemory { get; set; }
        }

        private readonly DropboxServiceClient _service;
        private readonly CacheOptions _options;
        private readonly string _accountId;
        private readonly string _email;
        private readonly string _accountIdHash;
        private readonly string _accountDir;
        private readonly string _dbPath;
        private readonly ConcurrentDictionary<string, Entry> _entries =
            new(StringComparer.OrdinalIgnoreCase);
        private readonly object _diskLock = new();
        private readonly SqliteConnection _db;
        private readonly Timer? _flushTimer;
        private bool _disposed;

        private static readonly JsonSerializerOptions JsonOpts = new()
        {
            WriteIndented = false,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        public MetadataCache(DropboxServiceClient service, string accountId,
            string? email, CacheOptions? options = null)
        {
            _service = service ?? throw new ArgumentNullException(nameof(service));
            _accountId = accountId ?? "";
            _email = email ?? "";
            _options = options ?? CacheOptions.Default;

            _accountIdHash = HashString(_accountId);
            _accountDir = _options.EffectiveRootDirectory;
            _dbPath = GetDatabasePath(_options, _email, _accountId);

            _db = OpenDatabase();

            if (_options.FlushIntervalSeconds > 0)
            {
                var period = TimeSpan.FromSeconds(_options.FlushIntervalSeconds);
                _flushTimer = new Timer(_ => SafeFlush(), null, period, period);
            }
        }

        public CacheOptions Options => _options;
        public string AccountId => _accountId;
        public string Email => _email;
        public string AccountIdHash => _accountIdHash;
        public string AccountDirectory => _accountDir;
        public string DatabasePath => _dbPath;

        /// <summary>Number of entries currently resident in memory.</summary>
        public int Count => _entries.Count;

        /// <summary>Total number of entries persisted to the database (the
        /// authoritative, unbounded cache size).</summary>
        public int PersistedCount()
        {
            lock (_diskLock)
            {
                using var cmd = _db.CreateCommand();
                cmd.CommandText = "SELECT COUNT(*) FROM entries;";
                var result = cmd.ExecuteScalar();
                return Convert.ToInt32(result, CultureInfo.InvariantCulture);
            }
        }

        /// <summary>In-memory snapshot (resident entries only).</summary>
        public IReadOnlyCollection<Entry> Snapshot() => _entries.Values.ToArray();

        /// <summary>
        /// Full observability snapshot: every persisted entry (read cheaply
        /// from the database, without its item list) overlaid with the current
        /// in-memory state for resident entries.
        /// </summary>
        public IReadOnlyCollection<EntryInfo> SnapshotInfo()
        {
            var byKey = new Dictionary<string, EntryInfo>(StringComparer.OrdinalIgnoreCase);

            lock (_diskLock)
            {
                using var cmd = _db.CreateCommand();
                cmd.CommandText =
                    "SELECT path_lower, path, cursor, item_count, last_validated_utc, last_used_utc FROM entries;";
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    var key = reader.GetString(0);
                    byKey[key] = new EntryInfo
                    {
                        Path = reader.GetString(1),
                        Cursor = reader.GetString(2),
                        ItemCount = reader.GetInt32(3),
                        LastValidatedUtc = ParseUtc(reader.GetString(4)),
                        LastUsedUtc = ParseUtc(reader.GetString(5)),
                        Dirty = false,
                        InMemory = false
                    };
                }
            }

            foreach (var entry in _entries.Values)
            {
                byKey[entry.PathLower] = new EntryInfo
                {
                    Path = entry.Path,
                    Cursor = entry.Cursor,
                    ItemCount = entry.Items.Count,
                    LastValidatedUtc = entry.LastValidatedUtc,
                    LastUsedUtc = entry.LastUsedUtc,
                    Dirty = entry.Dirty,
                    InMemory = true
                };
            }

            return byKey.Values.ToArray();
        }

        /// <summary>
        /// Returns the cached child items for <paramref name="path"/>,
        /// validating the cursor first. On cache miss, full-enumerates and
        /// stores the result. Always reflects the latest server state.
        /// </summary>
        public List<DropboxItem> GetChildren(string path, CancellationToken cancellationToken = default)
        {
            return GetChildrenAsync(path, cancellationToken).GetAwaiter().GetResult();
        }

        public async Task<List<DropboxItem>> GetChildrenAsync(string path, CancellationToken cancellationToken = default)
        {
            if (!_options.Enabled)
            {
                return await _service.ListFolderAsync(path, cancellationToken: cancellationToken);
            }

            var key = MakeKey(path);

            if (_entries.TryGetValue(key, out var entry))
            {
                await ValidateAsync(entry, cancellationToken);
                entry.LastUsedUtc = DateTime.UtcNow;
                return entry.Items.ToList();
            }

            // Warm hydrate: the path is not resident but may be persisted.
            if (TryLoadFromDisk(key, out var loaded) && loaded != null)
            {
                _entries[key] = loaded;
                await ValidateAsync(loaded, cancellationToken);
                loaded.LastUsedUtc = DateTime.UtcNow;
                EvictIfOverBudget();
                return loaded.Items.ToList();
            }

            // Cold miss: full enumeration.
            var (items, cursor) = await _service.ListFolderWithCursorAsync(path, cancellationToken: cancellationToken);
            entry = new Entry
            {
                Path = path,
                PathLower = key,
                Cursor = cursor,
                Items = items.ToList(),
                LastValidatedUtc = DateTime.UtcNow,
                LastUsedUtc = DateTime.UtcNow,
                Dirty = true
            };
            _entries[key] = entry;
            EvictIfOverBudget();
            return entry.Items.ToList();
        }

        /// <summary>Returns the cached entry for <paramref name="path"/> without
        /// validating, hydrating it from the database if necessary.</summary>
        public bool TryGet(string path, out Entry? entry)
        {
            entry = null;
            if (!_options.Enabled) return false;

            var key = MakeKey(path);
            if (_entries.TryGetValue(key, out entry)) return true;

            if (TryLoadFromDisk(key, out var loaded) && loaded != null)
            {
                _entries[key] = loaded;
                EvictIfOverBudget();
                entry = loaded;
                return true;
            }

            entry = null;
            return false;
        }

        /// <summary>Eagerly run validate-and-merge for a path (or all resident paths if null).</summary>
        public async Task UpdateAsync(string? path = null, CancellationToken cancellationToken = default)
        {
            if (path != null)
            {
                if (TryGet(path, out var e) && e != null)
                    await ValidateAsync(e, cancellationToken);
                return;
            }
            foreach (var e in _entries.Values.ToList())
                await ValidateAsync(e, cancellationToken);
        }

        /// <summary>Drop a path's entry (or all entries if null) from memory and disk.</summary>
        public void Clear(string? path = null)
        {
            if (path == null)
            {
                _entries.Clear();
                lock (_diskLock)
                {
                    using var cmd = _db.CreateCommand();
                    cmd.CommandText = "DELETE FROM entries;";
                    cmd.ExecuteNonQuery();
                }
                return;
            }

            var key = MakeKey(path);
            _entries.TryRemove(key, out _);
            DeleteRow(key);
        }

        // ----- write-through ---------------------------------------------------

        /// <summary>Mark the parent folder as dirty after a mutation. The
        /// next read will pick up the change via cursor validation; if the
        /// parent isn't cached yet we don't need to do anything.</summary>
        public void InvalidateParent(string childPath)
        {
            var parent = ParentOf(childPath);
            if (parent == null) return;
            var key = MakeKey(parent);
            _entries.TryRemove(key, out _);
            DeleteRow(key);
        }

        /// <summary>
        /// Apply a known local mutation to the parent folder's cached entry
        /// without invalidating it. Cheaper than <see cref="InvalidateParent"/>
        /// because the next read still only does a tiny continue() call.
        /// </summary>
        public void ApplyLocalAdd(DropboxItem item)
        {
            var parent = ParentOf(item.Path);
            if (parent == null) return;
            if (!_entries.TryGetValue(MakeKey(parent), out var entry)) return;
            ApplyAddTo(entry, item);
            entry.Dirty = true;
        }

        public void ApplyLocalRemove(string path)
        {
            var parent = ParentOf(path);
            if (parent == null) return;
            if (!_entries.TryGetValue(MakeKey(parent), out var entry)) return;
            var lowered = (path ?? "").ToLowerInvariant();
            entry.Items.RemoveAll(i => (i.Path ?? "").ToLowerInvariant() == lowered);
            entry.Dirty = true;
        }

        // ----- internals ------------------------------------------------------

        private async Task ValidateAsync(Entry entry, CancellationToken cancellationToken = default)
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (string.IsNullOrEmpty(entry.Cursor)) break;

                var delta = await _service.ListFolderContinueRawAsync(entry.Cursor, cancellationToken);
                if (delta.ResetRequired)
                {
                    var (items, newCursor) = await _service.ListFolderWithCursorAsync(entry.Path, cancellationToken: cancellationToken);
                    entry.Items = items.ToList();
                    entry.Cursor = newCursor;
                    entry.LastValidatedUtc = DateTime.UtcNow;
                    entry.Dirty = true;
                    break;
                }

                foreach (var add in delta.AddsOrUpdates) ApplyAddTo(entry, add);
                foreach (var rm in delta.Removes)
                    entry.Items.RemoveAll(i => (i.Path ?? "").ToLowerInvariant() == rm);

                entry.Cursor = delta.NewCursor;
                entry.LastValidatedUtc = DateTime.UtcNow;
                if (delta.AddsOrUpdates.Count > 0 || delta.Removes.Count > 0) entry.Dirty = true;

                if (!delta.HasMore) break;
            }
        }

        private static void ApplyAddTo(Entry entry, DropboxItem item)
        {
            var key = (item.Path ?? "").ToLowerInvariant();
            for (int i = 0; i < entry.Items.Count; i++)
            {
                if ((entry.Items[i].Path ?? "").ToLowerInvariant() == key)
                {
                    entry.Items[i] = item;
                    return;
                }
            }
            entry.Items.Add(item);
        }

        /// <summary>
        /// Keep the in-memory working set within budget. Victims are flushed to
        /// the database first (so nothing is lost) and then dropped from memory;
        /// they re-hydrate on next access. The persistent cache is never capped.
        /// </summary>
        private void EvictIfOverBudget()
        {
            var budget = _options.MaxInMemoryEntries;
            if (budget <= 0 || _entries.Count <= budget) return;

            var victims = _entries.Values
                .OrderBy(e => e.LastUsedUtc)
                .Take(_entries.Count - budget)
                .ToList();

            foreach (var v in victims)
            {
                if (v.Dirty) PersistEntry(v);
                _entries.TryRemove(v.PathLower, out _);
            }
        }

        // ----- disk persistence (SQLite) -------------------------------------

        private SqliteConnection OpenDatabase()
        {
            var databaseDirectory = Path.GetDirectoryName(_dbPath);
            Directory.CreateDirectory(
                string.IsNullOrEmpty(databaseDirectory) ? _accountDir : databaseDirectory);
            var connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = _dbPath,
                Pooling = false
            }.ToString();

            var connection = new SqliteConnection(connectionString);
            connection.Open();

            using (var pragma = connection.CreateCommand())
            {
                pragma.CommandText =
                    "PRAGMA journal_mode=WAL;" +
                    "PRAGMA synchronous=NORMAL;" +
                    "CREATE TABLE IF NOT EXISTS entries (" +
                    "  path_lower TEXT PRIMARY KEY," +
                    "  path TEXT NOT NULL," +
                    "  cursor TEXT NOT NULL," +
                    "  items_json TEXT NOT NULL," +
                    "  item_count INTEGER NOT NULL," +
                    "  last_validated_utc TEXT NOT NULL," +
                    "  last_used_utc TEXT NOT NULL" +
                    ");";
                pragma.ExecuteNonQuery();
            }

            return connection;
        }

        private bool TryLoadFromDisk(string key, out Entry? entry)
        {
            entry = null;
            lock (_diskLock)
            {
                using var cmd = _db.CreateCommand();
                cmd.CommandText =
                    "SELECT path, cursor, items_json, last_validated_utc, last_used_utc " +
                    "FROM entries WHERE path_lower = $k;";
                cmd.Parameters.AddWithValue("$k", key);

                using var reader = cmd.ExecuteReader();
                if (!reader.Read()) return false;

                List<DropboxItem> items;
                try
                {
                    items = JsonSerializer.Deserialize<List<DropboxItem>>(reader.GetString(2), JsonOpts)
                            ?? new List<DropboxItem>();
                }
                catch
                {
                    return false; // skip corrupt row
                }

                entry = new Entry
                {
                    Path = reader.GetString(0),
                    PathLower = key,
                    Cursor = reader.GetString(1),
                    Items = items,
                    LastValidatedUtc = ParseUtc(reader.GetString(3)),
                    LastUsedUtc = ParseUtc(reader.GetString(4)),
                    Dirty = false
                };
                return true;
            }
        }

        private void PersistEntry(Entry entry)
        {
            lock (_diskLock)
            {
                UpsertEntry(entry);
                entry.Dirty = false;
            }
        }

        private void UpsertEntry(Entry entry)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText =
                "INSERT INTO entries (path_lower, path, cursor, items_json, item_count, last_validated_utc, last_used_utc) " +
                "VALUES ($k, $p, $c, $j, $n, $lv, $lu) " +
                "ON CONFLICT(path_lower) DO UPDATE SET " +
                "  path=excluded.path, cursor=excluded.cursor, items_json=excluded.items_json, " +
                "  item_count=excluded.item_count, last_validated_utc=excluded.last_validated_utc, " +
                "  last_used_utc=excluded.last_used_utc;";
            cmd.Parameters.AddWithValue("$k", entry.PathLower);
            cmd.Parameters.AddWithValue("$p", entry.Path);
            cmd.Parameters.AddWithValue("$c", entry.Cursor ?? "");
            cmd.Parameters.AddWithValue("$j", JsonSerializer.Serialize(entry.Items, JsonOpts));
            cmd.Parameters.AddWithValue("$n", entry.Items.Count);
            cmd.Parameters.AddWithValue("$lv", FormatUtc(entry.LastValidatedUtc));
            cmd.Parameters.AddWithValue("$lu", FormatUtc(entry.LastUsedUtc));
            cmd.ExecuteNonQuery();
        }

        private void DeleteRow(string key)
        {
            lock (_diskLock)
            {
                using var cmd = _db.CreateCommand();
                cmd.CommandText = "DELETE FROM entries WHERE path_lower = $k;";
                cmd.Parameters.AddWithValue("$k", key);
                cmd.ExecuteNonQuery();
            }
        }

        /// <summary>Persist all dirty resident entries to the database.</summary>
        public void Flush()
        {
            lock (_diskLock)
            {
                var dirty = _entries.Values.Where(e => e.Dirty).ToList();
                if (dirty.Count == 0) return;

                using var tx = _db.BeginTransaction();
                foreach (var entry in dirty)
                {
                    UpsertEntry(entry);
                    entry.Dirty = false;
                }
                tx.Commit();
            }
        }

        private void SafeFlush()
        {
            try { Flush(); } catch { /* best effort */ }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try { _flushTimer?.Dispose(); } catch { }
            try { Flush(); } catch { }
            try { _db.Dispose(); } catch { }
        }

        // ----- helpers --------------------------------------------------------

        private static string MakeKey(string path)
        {
            var norm = DropboxServiceClient.NormalizePath(path);
            return norm.ToLowerInvariant();
        }

        private static string? ParentOf(string path)
        {
            var norm = DropboxServiceClient.NormalizePath(path);
            if (string.IsNullOrEmpty(norm)) return null;
            var idx = norm.LastIndexOf('/');
            if (idx <= 0) return "";
            return norm.Substring(0, idx);
        }

        private static string FormatUtc(DateTime value) =>
            value.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture);

        private static DateTime ParseUtc(string value) =>
            DateTime.TryParse(value, CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind, out var dt)
                ? dt
                : DateTime.MinValue;

        /// <summary>
        /// Resolves the on-disk database path for an account under the supplied
        /// options. If <paramref name="email"/> is non-empty and has an entry in
        /// <see cref="CacheOptions.EmailDatabasePathOverrides"/>, the configured
        /// path is expanded (leading <c>~</c> -> user profile, environment
        /// variables) and made absolute, then used verbatim. A configured path
        /// that names an existing directory or ends in a separator gets the
        /// default <c>DropboxCache.&lt;email&gt;.db</c> file placed inside it.
        /// Otherwise the path falls back to
        /// <c>&lt;EffectiveRootDirectory&gt;\DropboxCache.&lt;email-or-hash&gt;.db</c>.
        /// </summary>
        /// <param name="options">Cache options carrying any overrides and the root.</param>
        /// <param name="email">Account email (case-insensitive override key).</param>
        /// <param name="accountId">Account id, hashed for the empty-email fallback.</param>
        public static string GetDatabasePath(CacheOptions options, string? email, string? accountId)
        {
            if (options == null)
            {
                throw new ArgumentNullException(nameof(options));
            }

            var resolvedEmail = email ?? "";
            var accountIdHash = HashString(accountId ?? "");

            if (!string.IsNullOrWhiteSpace(resolvedEmail) &&
                options.EmailDatabasePathOverrides.TryGetValue(resolvedEmail, out var configured) &&
                !string.IsNullOrWhiteSpace(configured))
            {
                return ResolveOverridePath(configured, resolvedEmail, accountIdHash);
            }

            return Path.Combine(options.EffectiveRootDirectory,
                BuildDatabaseFileName(resolvedEmail, accountIdHash));
        }

        /// <summary>
        /// Expands and absolutizes a configured override path. A directory-like
        /// path receives the default database file name; a file path is used as-is.
        /// </summary>
        private static string ResolveOverridePath(string configured, string email, string accountIdHash)
        {
            var expanded = ExpandHomeAndEnvironment(configured);
            var treatAsDirectory = EndsInDirectorySeparator(expanded) || Directory.Exists(expanded);
            var full = Path.GetFullPath(expanded);
            return treatAsDirectory
                ? Path.Combine(full, BuildDatabaseFileName(email, accountIdHash))
                : full;
        }

        /// <summary>
        /// Expands a leading <c>~</c> (alone, or followed by <c>/</c> or <c>\</c>)
        /// to the user profile directory, then expands environment variables. The
        /// result is not yet absolutized.
        /// </summary>
        private static string ExpandHomeAndEnvironment(string path)
        {
            var trimmed = path.Trim();
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (trimmed == "~")
            {
                trimmed = home;
            }
            else if (trimmed.StartsWith("~/", StringComparison.Ordinal) ||
                     trimmed.StartsWith("~\\", StringComparison.Ordinal))
            {
                trimmed = Path.Combine(home, trimmed.Substring(2));
            }

            return Environment.ExpandEnvironmentVariables(trimmed);
        }

        private static bool EndsInDirectorySeparator(string path) =>
            path.Length > 0 &&
            (path[path.Length - 1] == Path.DirectorySeparatorChar ||
             path[path.Length - 1] == Path.AltDirectorySeparatorChar);

        private static string HashString(string input)
        {
            using var sha = SHA256.Create();
            var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(input ?? ""));
            return BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant();
        }

        /// <summary>
        /// Builds the single-file database name placed directly in the cache
        /// root. When an email is available the file is named
        /// <c>DropboxCache.&lt;sanitized-email&gt;.db</c>; otherwise a SHA-256 hash
        /// of the account id is used so multi-account isolation is preserved and
        /// a malformed name like <c>DropboxCache..db</c> is never produced.
        /// </summary>
        private static string BuildDatabaseFileName(string email, string accountIdHash)
        {
            var label = string.IsNullOrWhiteSpace(email)
                ? accountIdHash
                : SanitizeForFileName(email);
            return $"DropboxCache.{label}.db";
        }

        /// <summary>
        /// Lowercases the email and replaces any character that is invalid in a
        /// file name with <c>_</c>. Email-legal characters such as <c>@ . + -</c>
        /// are preserved.
        /// </summary>
        private static string SanitizeForFileName(string email)
        {
            var lower = email.Trim().ToLowerInvariant();
            var invalid = Path.GetInvalidFileNameChars();
            var builder = new StringBuilder(lower.Length);
            foreach (var ch in lower)
            {
                builder.Append(Array.IndexOf(invalid, ch) >= 0 ? '_' : ch);
            }
            return builder.ToString();
        }
    }
}
