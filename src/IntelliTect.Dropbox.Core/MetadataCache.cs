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
    /// under <c>%LOCALAPPDATA%\DbxProvider\</c> (the same directory that holds
    /// <c>config.json</c>; no redundant <c>cache</c> subfolder is used). The
    /// file is named
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

        /// <summary>Outcome of a <see cref="BuildAsync"/> (and optional
        /// <see cref="BuildRevisionsAsync"/>) pre-population pass.</summary>
        public sealed class BuildResult
        {
            /// <summary>Number of per-folder cache entries created or refreshed
            /// during this invocation.</summary>
            public int FoldersCached { get; set; }

            /// <summary>Number of descendant items processed during this
            /// invocation. For a resumed build this counts only the items in the
            /// pages walked this call, not pages persisted by an earlier run.</summary>
            public int ItemsFound { get; set; }

            /// <summary>Number of files whose revision history was fetched and
            /// cached. Populated only by <see cref="BuildRevisionsAsync"/>.</summary>
            public int FilesWithRevisionsCached { get; set; }

            /// <summary>Total number of revision rows cached across all files.
            /// Populated only by <see cref="BuildRevisionsAsync"/>.</summary>
            public int RevisionsCached { get; set; }
        }

        /// <summary>Per-invocation accumulator for a subtree build (page-by-page).</summary>
        private sealed class BuildState
        {
            public BuildState(string rootPath)
            {
                RootPath = rootPath;
                RootKey = MakeKey(rootPath);
                Folders.Add(RootKey);
            }

            public string RootPath { get; }
            public string RootKey { get; }
            public HashSet<string> Folders { get; } = new(StringComparer.OrdinalIgnoreCase);
            public int ItemsFound { get; set; }
        }

        /// <summary>Persisted state of an in-progress or completed subtree build.</summary>
        private sealed class BuildProgress
        {
            public BuildProgress(string cursor, bool complete)
            {
                Cursor = cursor;
                Complete = complete;
            }

            public string Cursor { get; }
            public bool Complete { get; }
        }

        /// <summary>Default staleness window for revision re-fetch: a file's
        /// revisions are refreshed only when its last fetch is older than this.</summary>
        private static readonly TimeSpan DefaultRevisionMaxAge = TimeSpan.FromHours(24);

        /// <summary>
        /// Pre-populates the cache for an entire subtree by walking a recursive
        /// <c>list_folder</c> one page at a time, grouping each page by parent
        /// folder and flushing to SQLite after every page. The in-progress
        /// cursor is persisted so an interrupted build resumes from the last
        /// completed page on the next call. Enriched metadata is requested at no
        /// extra request cost.
        /// </summary>
        public async Task<BuildResult> BuildAsync(string path, CancellationToken cancellationToken = default)
        {
            if (!_options.Enabled) return new BuildResult();

            var state = new BuildState(DropboxServiceClient.NormalizePath(path));
            var saved = LoadBuildProgress(state.RootKey);
            if (saved is { Complete: false } && saved.Cursor.Length > 0)
                await ContinueBuildAsync(state, saved.Cursor, cancellationToken);
            else
                await FreshBuildAsync(state, cancellationToken);

            EvictIfOverBudget();
            return new BuildResult
            {
                FoldersCached = state.Folders.Count,
                ItemsFound = state.ItemsFound
            };
        }

        /// <summary>Starts a build from the first page of a recursive listing,
        /// requesting enriched metadata at no extra request cost.</summary>
        private async Task FreshBuildAsync(BuildState state, CancellationToken ct)
        {
            GetOrCreateBuildEntry(state.RootKey, state.RootPath);
            SaveBuildProgress(state.RootKey, cursor: "", complete: false);

            var page = await _service.ListFolderFirstPageAsync(state.RootPath, recursive: true,
                includeMediaInfo: true, includeHasExplicitSharedMembers: true,
                cancellationToken: ct);

            ProcessPage(state, page.Items, page.Cursor, complete: !page.HasMore);
            if (page.HasMore) await ContinueBuildAsync(state, page.Cursor, ct);
        }

        /// <summary>Continues a build from a saved cursor, restarting cleanly when
        /// Dropbox signals that the cursor is no longer valid.</summary>
        private async Task ContinueBuildAsync(BuildState state, string cursor, CancellationToken ct)
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                var delta = await _service.ListFolderContinueRawAsync(cursor, ct);
                if (delta.ResetRequired)
                {
                    await FreshBuildAsync(state, ct);
                    return;
                }

                ProcessPage(state, delta.AddsOrUpdates, delta.NewCursor, complete: !delta.HasMore);
                cursor = delta.NewCursor;
                if (!delta.HasMore) return;
            }
        }

        /// <summary>Merges one page into the in-memory entries and flushes the
        /// page plus the advanced cursor to SQLite in a single transaction.</summary>
        private void ProcessPage(BuildState state, List<DropboxItem> items, string cursor, bool complete)
        {
            foreach (var item in items) MergeBuildItem(state, item);
            PersistBuildPage(state.RootKey, cursor, complete);
        }

        /// <summary>Adds a single listed item to its parent folder entry and, when
        /// the item is itself a folder, ensures an entry exists for it too.</summary>
        private void MergeBuildItem(BuildState state, DropboxItem item)
        {
            state.ItemsFound++;
            var parentKey = BuildParentKey(item.Path, state.RootPath);
            var parent = GetOrCreateBuildEntry(parentKey, BuildParentDisplay(item.Path, state.RootPath));
            ApplyAddTo(parent, item);
            parent.Dirty = true;
            state.Folders.Add(parentKey);
            if (!item.IsFolder) return;

            GetOrCreateBuildEntry(MakeKey(item.Path), item.Path);
            state.Folders.Add(MakeKey(item.Path));
        }

        /// <summary>Resolves the cache key of the folder that owns an item.</summary>
        private static string BuildParentKey(string path, string rootPath)
        {
            var parent = ParentOf(path);
            return MakeKey(string.IsNullOrEmpty(parent) ? rootPath : parent);
        }

        /// <summary>Resolves the display path of the folder that owns an item.</summary>
        private static string BuildParentDisplay(string path, string rootPath)
        {
            var parent = ParentOf(path);
            return string.IsNullOrEmpty(parent) ? rootPath : parent;
        }

        /// <summary>Returns the in-memory entry for a folder, hydrating it from
        /// disk or creating a fresh empty entry when absent.</summary>
        private Entry GetOrCreateBuildEntry(string key, string displayPath)
        {
            if (_entries.TryGetValue(key, out var existing)) return existing;
            if (TryLoadFromDisk(key, out var loaded) && loaded != null)
            {
                _entries[key] = loaded;
                return loaded;
            }

            var now = DateTime.UtcNow;
            var entry = new Entry
            {
                Path = displayPath,
                PathLower = key,
                Cursor = "",
                Items = new List<DropboxItem>(),
                LastValidatedUtc = now,
                LastUsedUtc = now,
                Dirty = true
            };
            _entries[key] = entry;
            return entry;
        }

        /// <summary>
        /// Fetches and caches the revision history of every file in a subtree.
        /// Files whose revisions were fetched within <paramref name="maxAge"/>
        /// are skipped, so an interrupted pass resumes cheaply.
        /// </summary>
        public async Task<BuildResult> BuildRevisionsAsync(string path,
            Action<int, int>? onProgress = null, TimeSpan? maxAge = null,
            CancellationToken cancellationToken = default)
        {
            if (!_options.Enabled) return new BuildResult();

            var rootPath = DropboxServiceClient.NormalizePath(path);
            var files = CollectSubtreeFiles(rootPath);
            var staleness = maxAge ?? DefaultRevisionMaxAge;
            var result = new BuildResult();
            for (int i = 0; i < files.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                onProgress?.Invoke(i, files.Count);
                await EnrichFileRevisionsAsync(files[i], staleness, result, cancellationToken);
            }

            onProgress?.Invoke(files.Count, files.Count);
            return result;
        }

        /// <summary>Fetches and persists revisions for one file unless a recent
        /// fetch already satisfies the staleness window.</summary>
        private async Task EnrichFileRevisionsAsync(string filePath, TimeSpan staleness,
            BuildResult result, CancellationToken ct)
        {
            var key = MakeKey(filePath);
            if (IsRevisionFresh(key, staleness)) return;

            var revisions = await _service.ListRevisionsAsync(filePath, cancellationToken: ct);
            PersistRevisions(key, revisions);
            result.FilesWithRevisionsCached++;
            result.RevisionsCached += revisions.Count;
        }

        /// <summary>Walks the cached subtree depth-first and returns every file
        /// path, skipping folders.</summary>
        private List<string> CollectSubtreeFiles(string rootPath)
        {
            var files = new List<string>();
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var pending = new Stack<string>();
            pending.Push(rootPath);
            while (pending.Count > 0)
            {
                var folder = pending.Pop();
                if (!visited.Add(MakeKey(folder)) || !TryGet(folder, out var entry) || entry == null) continue;
                foreach (var item in entry.Items)
                {
                    if (item.IsFolder) pending.Push(item.Path);
                    else files.Add(item.Path);
                }
            }

            return files;
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

                // A recursively-built entry has no per-folder cursor yet; acquire
                // one via a fresh listing so Dropbox stays the master before the
                // entry is served.
                if (string.IsNullOrEmpty(entry.Cursor))
                {
                    await RefreshFromServerAsync(entry, cancellationToken);
                    break;
                }

                var delta = await _service.ListFolderContinueRawAsync(entry.Cursor, cancellationToken);
                if (delta.ResetRequired)
                {
                    await RefreshFromServerAsync(entry, cancellationToken);
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

        /// <summary>Replaces an entry's items and cursor from a fresh
        /// non-recursive listing of its path.</summary>
        private async Task RefreshFromServerAsync(Entry entry, CancellationToken cancellationToken)
        {
            var (items, newCursor) = await _service.ListFolderWithCursorAsync(
                entry.Path, cancellationToken: cancellationToken);
            entry.Items = items.ToList();
            entry.Cursor = newCursor;
            entry.LastValidatedUtc = DateTime.UtcNow;
            entry.Dirty = true;
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
                    ");" +
                    "CREATE TABLE IF NOT EXISTS build_progress (" +
                    "  root_path_lower TEXT PRIMARY KEY," +
                    "  cursor TEXT NOT NULL," +
                    "  updated_utc TEXT NOT NULL," +
                    "  complete INTEGER NOT NULL DEFAULT 0" +
                    ");" +
                    "CREATE TABLE IF NOT EXISTS revisions (" +
                    "  path_lower TEXT NOT NULL," +
                    "  rev TEXT NOT NULL," +
                    "  length INTEGER NOT NULL," +
                    "  content_hash TEXT NOT NULL," +
                    "  server_modified TEXT NOT NULL," +
                    "  client_modified TEXT NOT NULL," +
                    "  is_deleted INTEGER NOT NULL DEFAULT 0," +
                    "  fetched_utc TEXT NOT NULL," +
                    "  PRIMARY KEY (path_lower, rev)" +
                    ");" +
                    "CREATE TABLE IF NOT EXISTS revision_progress (" +
                    "  path_lower TEXT PRIMARY KEY," +
                    "  fetched_utc TEXT NOT NULL" +
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

        /// <summary>Loads the persisted build progress for a subtree root, or
        /// <c>null</c> when no build has been recorded.</summary>
        private BuildProgress? LoadBuildProgress(string rootKey)
        {
            lock (_diskLock)
            {
                using var cmd = _db.CreateCommand();
                cmd.CommandText =
                    "SELECT cursor, complete FROM build_progress WHERE root_path_lower = $k;";
                cmd.Parameters.AddWithValue("$k", rootKey);
                using var reader = cmd.ExecuteReader();
                if (!reader.Read()) return null;
                return new BuildProgress(reader.GetString(0), reader.GetInt64(1) != 0);
            }
        }

        /// <summary>Indicates whether a completed build is recorded for a path.</summary>
        public bool IsBuildComplete(string path) =>
            LoadBuildProgress(MakeKey(DropboxServiceClient.NormalizePath(path))) is { Complete: true };

        /// <summary>Persists the build progress row for a subtree root.</summary>
        private void SaveBuildProgress(string rootKey, string cursor, bool complete)
        {
            lock (_diskLock)
            {
                UpsertBuildProgress(rootKey, cursor, complete);
            }
        }

        /// <summary>Inserts or updates the build progress row. The caller holds
        /// the disk lock; any active transaction is auto-enlisted.</summary>
        private void UpsertBuildProgress(string rootKey, string cursor, bool complete)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText =
                "INSERT INTO build_progress (root_path_lower, cursor, updated_utc, complete) " +
                "VALUES ($k, $c, $u, $done) " +
                "ON CONFLICT(root_path_lower) DO UPDATE SET " +
                "  cursor=excluded.cursor, updated_utc=excluded.updated_utc, complete=excluded.complete;";
            cmd.Parameters.AddWithValue("$k", rootKey);
            cmd.Parameters.AddWithValue("$c", cursor);
            cmd.Parameters.AddWithValue("$u", FormatUtc(DateTime.UtcNow));
            cmd.Parameters.AddWithValue("$done", complete ? 1 : 0);
            cmd.ExecuteNonQuery();
        }

        /// <summary>Flushes the dirty entries accumulated for one page and the
        /// advanced cursor to SQLite in a single transaction.</summary>
        private void PersistBuildPage(string rootKey, string cursor, bool complete)
        {
            lock (_diskLock)
            {
                using var tx = _db.BeginTransaction();
                foreach (var entry in _entries.Values)
                {
                    if (!entry.Dirty) continue;
                    UpsertEntry(entry);
                    entry.Dirty = false;
                }
                UpsertBuildProgress(rootKey, cursor, complete);
                tx.Commit();
            }
        }

        /// <summary>Indicates whether a file's revisions were fetched recently
        /// enough to skip re-fetching.</summary>
        private bool IsRevisionFresh(string fileKey, TimeSpan maxAge)
        {
            lock (_diskLock)
            {
                using var cmd = _db.CreateCommand();
                cmd.CommandText =
                    "SELECT fetched_utc FROM revision_progress WHERE path_lower = $k;";
                cmd.Parameters.AddWithValue("$k", fileKey);
                if (cmd.ExecuteScalar() is not string value) return false;
                return DateTime.UtcNow - ParseUtc(value) < maxAge;
            }
        }

        /// <summary>Persists the revisions of one file and records the fetch time
        /// in a single transaction.</summary>
        private void PersistRevisions(string fileKey, List<DropboxRevision> revisions)
        {
            lock (_diskLock)
            {
                using var tx = _db.BeginTransaction();
                foreach (var revision in revisions) UpsertRevision(fileKey, revision);
                UpsertRevisionProgress(fileKey);
                tx.Commit();
            }
        }

        /// <summary>Inserts or updates a single revision row.</summary>
        private void UpsertRevision(string fileKey, DropboxRevision revision)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText =
                "INSERT INTO revisions (path_lower, rev, length, content_hash, server_modified, client_modified, is_deleted, fetched_utc) " +
                "VALUES ($k, $r, $len, $hash, $sm, $cm, $del, $f) " +
                "ON CONFLICT(path_lower, rev) DO UPDATE SET " +
                "  length=excluded.length, content_hash=excluded.content_hash, " +
                "  server_modified=excluded.server_modified, client_modified=excluded.client_modified, " +
                "  is_deleted=excluded.is_deleted, fetched_utc=excluded.fetched_utc;";
            cmd.Parameters.AddWithValue("$k", fileKey);
            cmd.Parameters.AddWithValue("$r", revision.Rev);
            cmd.Parameters.AddWithValue("$len", (long)revision.Length);
            cmd.Parameters.AddWithValue("$hash", revision.ContentHash);
            cmd.Parameters.AddWithValue("$sm", FormatUtcNullable(revision.ServerModified));
            cmd.Parameters.AddWithValue("$cm", FormatUtcNullable(revision.ClientModified));
            cmd.Parameters.AddWithValue("$del", revision.IsDeleted ? 1 : 0);
            cmd.Parameters.AddWithValue("$f", FormatUtc(DateTime.UtcNow));
            cmd.ExecuteNonQuery();
        }

        /// <summary>Inserts or updates the revision fetch marker for a file.</summary>
        private void UpsertRevisionProgress(string fileKey)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText =
                "INSERT INTO revision_progress (path_lower, fetched_utc) VALUES ($k, $f) " +
                "ON CONFLICT(path_lower) DO UPDATE SET fetched_utc=excluded.fetched_utc;";
            cmd.Parameters.AddWithValue("$k", fileKey);
            cmd.Parameters.AddWithValue("$f", FormatUtc(DateTime.UtcNow));
            cmd.ExecuteNonQuery();
        }

        /// <summary>Returns the total number of cached revision rows.</summary>
        public long RevisionCount()
        {
            lock (_diskLock)
            {
                using var cmd = _db.CreateCommand();
                cmd.CommandText = "SELECT COUNT(*) FROM revisions;";
                return (long)(cmd.ExecuteScalar() ?? 0L);
            }
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

        private static string FormatUtcNullable(DateTime? value) =>
            value.HasValue ? FormatUtc(value.Value) : "";

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
