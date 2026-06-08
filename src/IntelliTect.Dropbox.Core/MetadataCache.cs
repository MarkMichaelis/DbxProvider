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

        /// <summary>
        /// Account-wide incremental-sync anchor: the recursive delta cursor
        /// captured at build start, together with when it was captured and when it
        /// was last drained by <see cref="SyncAsync"/>.
        /// </summary>
        public sealed class SyncStateInfo
        {
            /// <summary>The captured (or last-advanced) account delta cursor.</summary>
            public string Cursor { get; set; } = "";

            /// <summary>UTC time the original cursor was captured (build start).</summary>
            public DateTime CapturedUtc { get; set; }

            /// <summary>UTC time of the most recent successful drain, or
            /// <c>null</c> when the cursor has never been drained.</summary>
            public DateTime? LastSyncedUtc { get; set; }
        }

        /// <summary>Outcome of an incremental <see cref="SyncAsync"/> drain.</summary>
        public sealed class SyncResult
        {
            /// <summary>Number of delta adds/updates applied to cache entries.</summary>
            public int Added { get; set; }

            /// <summary>Number of delta removes applied to cache entries.</summary>
            public int Removed { get; set; }

            /// <summary>Number of <c>list_folder/continue</c> pages drained.</summary>
            public int Pages { get; set; }

            /// <summary>True when Dropbox rejected the cursor; the caller must
            /// rebuild the cache (the deltas could not be applied incrementally).</summary>
            public bool ResetRequired { get; set; }
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
        /// Streams every cached item at or below <paramref name="startPath"/>
        /// straight from the local database, without contacting Dropbox. Dirty
        /// resident entries are flushed first so the on-disk view is
        /// authoritative, then each folder's item list is read and yielded one
        /// entry at a time -- the full item set (which can be millions of rows)
        /// is never materialized at once. The database lock is taken per entry
        /// and released before any item is yielded, so a consumer may safely
        /// call back into the cache while enumerating.
        /// </summary>
        /// <param name="startPath">Subtree root; empty or "/" enumerates the
        /// whole account.</param>
        public IEnumerable<DropboxItem> EnumerateItems(string startPath = "")
        {
            Flush();

            var startKey = MakeKey(startPath);
            foreach (var key in LoadEntryKeysUnder(startKey))
            {
                var items = TryLoadEntryItems(key);
                if (items == null) continue;
                foreach (var item in items)
                {
                    yield return item;
                }
            }
        }

        /// <summary>
        /// Returns every cached item at or below <paramref name="startPath"/>
        /// that satisfies <paramref name="predicate"/>, de-duplicated by path
        /// (case-insensitive). Reads the local database only; issues no Dropbox
        /// API calls.
        /// </summary>
        public List<DropboxItem> FindItems(Func<DropboxItem, bool> predicate, string startPath = "")
        {
            if (predicate == null) throw new ArgumentNullException(nameof(predicate));

            var results = new List<DropboxItem>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in EnumerateItems(startPath))
            {
                if (predicate(item) && seen.Add(item.Path))
                {
                    results.Add(item);
                }
            }
            return results;
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

            var result = new BuildResult();
            await BuildSubtreeAsync(DropboxServiceClient.NormalizePath(path), result, cancellationToken);
            EvictIfOverBudget();
            return result;
        }

        /// <summary>Builds one subtree via a single recursive listing, falling
        /// back to per-subfolder descent when that listing wedges (never returns
        /// a page within the configured wedge timeout). The fallback lists the
        /// folder one level at a time -- a bounded call that cannot wedge -- and
        /// recurses into each subfolder, so a huge folder nested at any depth is
        /// handled and the walk always terminates.</summary>
        private async Task BuildSubtreeAsync(string rootPath, BuildResult result, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();

            var state = new BuildState(rootPath);
            var saved = LoadBuildProgress(state.RootKey);
            if (saved is { Complete: true }) return;

            var completed = saved is { Complete: false } && saved.Cursor.Length > 0
                ? await ContinueBuildAsync(state, saved.Cursor, ct)
                : await FreshBuildAsync(state, ct);

            result.FoldersCached += state.Folders.Count;
            result.ItemsFound += state.ItemsFound;
            if (completed) return;

            // The recursive listing wedged: cache this folder's direct children
            // with a bounded non-recursive listing, then recurse into each
            // subfolder using the same adaptive strategy.
            var children = await ListAndCacheDirectChildrenAsync(rootPath, ct);
            foreach (var child in children)
            {
                if (child.IsFolder)
                    await BuildSubtreeAsync(DropboxServiceClient.NormalizePath(child.Path), result, ct);
            }

            SaveBuildProgress(state.RootKey, cursor: string.Empty, complete: true);
        }

        /// <summary>Starts a build from the first page of a recursive listing,
        /// requesting enriched metadata at no extra request cost. Returns
        /// <c>true</c> when the subtree was fully listed, or <c>false</c> when a
        /// listing call wedged and the caller should descend instead.</summary>
        private async Task<bool> FreshBuildAsync(BuildState state, CancellationToken ct)
        {
            GetOrCreateBuildEntry(state.RootKey, state.RootPath);
            SaveBuildProgress(state.RootKey, cursor: "", complete: false);

            var page = await RunBoundedAsync(c => _service.ListFolderFirstPageAsync(state.RootPath,
                recursive: true, includeMediaInfo: true, includeHasExplicitSharedMembers: true,
                cancellationToken: c), ct);
            if (page is null) return false;

            ProcessPage(state, page.Items, page.Cursor, complete: !page.HasMore);
            if (!page.HasMore) return true;
            return await ContinueBuildAsync(state, page.Cursor, ct);
        }

        /// <summary>Continues a build from a saved cursor, restarting cleanly when
        /// Dropbox signals that the cursor is no longer valid. Returns <c>true</c>
        /// when the subtree was fully listed, or <c>false</c> when a continue call
        /// wedged and the caller should descend instead.</summary>
        private async Task<bool> ContinueBuildAsync(BuildState state, string cursor, CancellationToken ct)
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                var delta = await RunBoundedAsync(c => _service.ListFolderContinueRawAsync(cursor, c), ct);
                if (delta is null) return false;
                if (delta.ResetRequired)
                    return await FreshBuildAsync(state, ct);

                ProcessPage(state, delta.AddsOrUpdates, delta.NewCursor, complete: !delta.HasMore);
                cursor = delta.NewCursor;
                if (!delta.HasMore) return true;
            }
        }

        /// <summary>Awaits a listing call but treats one that does not return
        /// within <see cref="CacheOptions.BuildWedgeTimeoutSeconds"/> as a wedge:
        /// it cancels the stalled call and returns <c>null</c> so the caller can
        /// fall back to per-subfolder descent. A timeout of zero awaits directly.
        /// Outer cancellation propagates as cancellation, not as a wedge.</summary>
        private async Task<T?> RunBoundedAsync<T>(Func<CancellationToken, Task<T>> operation,
            CancellationToken ct) where T : class
        {
            var seconds = _options.BuildWedgeTimeoutSeconds;
            if (seconds <= 0) return await operation(ct);

            using var operationCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            using var delayCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var operationTask = operation(operationCts.Token);
            var delayTask = Task.Delay(TimeSpan.FromSeconds(seconds), delayCts.Token);

            if (await Task.WhenAny(operationTask, delayTask) == operationTask)
            {
                delayCts.Cancel();
                return await operationTask;
            }

            ct.ThrowIfCancellationRequested();
            operationCts.Cancel();
            try { await operationTask; } catch { /* stalled call cancelled */ }
            return null;
        }

        /// <summary>Lists a folder's immediate children with a bounded
        /// non-recursive call and stores them as that folder's cache entry. Used
        /// by the descend-on-wedge fallback; the listing is bounded by the
        /// folder's direct child count and therefore cannot wedge.</summary>
        private async Task<List<DropboxItem>> ListAndCacheDirectChildrenAsync(string rootPath,
            CancellationToken ct)
        {
            var (items, cursor) = await _service.ListFolderWithCursorAsync(rootPath,
                cancellationToken: ct);

            var entry = GetOrCreateBuildEntry(MakeKey(rootPath), rootPath);
            entry.Items = items.ToList();
            entry.Cursor = cursor;
            entry.LastValidatedUtc = DateTime.UtcNow;
            entry.LastUsedUtc = DateTime.UtcNow;
            entry.Dirty = true;
            foreach (var item in items)
            {
                if (item.IsFolder)
                    GetOrCreateBuildEntry(MakeKey(item.Path), item.Path);
            }

            Flush();
            return items;
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
            return MakeKey(string.IsNullOrEmpty(parent) ? rootPath : parent!);
        }

        /// <summary>Resolves the display path of the folder that owns an item.</summary>
        private static string BuildParentDisplay(string path, string rootPath)
        {
            var parent = ParentOf(path);
            return string.IsNullOrEmpty(parent) ? rootPath : parent!;
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

        // ----- incremental sync (account-wide delta cursor) -------------------

        /// <summary>
        /// Captures the account-wide recursive delta cursor as the incremental-sync
        /// anchor, but ONLY when none has been captured yet (capture-if-absent).
        /// Call this at the start of a build so the cursor marks the pre-build
        /// state; a later <see cref="SyncAsync"/> then replays every change since,
        /// including changes made during the build itself. Returns <c>true</c> when
        /// a new cursor was captured, or <c>false</c> when one already existed and
        /// was left untouched (so resuming an interrupted build keeps the original
        /// start cursor).
        /// </summary>
        public async Task<bool> EnsureSyncCursorAsync(CancellationToken cancellationToken = default)
        {
            if (!_options.Enabled) return false;
            if (GetSyncState() != null) return false;
            var cursor = await _service.GetLatestCursorAsync("", recursive: true, cancellationToken);
            SaveSyncState(cursor, DateTime.UtcNow, lastSyncedUtc: "");
            return true;
        }

        /// <summary>
        /// Discards any captured sync cursor and captures a fresh one for the
        /// current account state. Used by a full rebuild so the new cursor anchors
        /// the rebuilt cache.
        /// </summary>
        public async Task ResetSyncCursorAsync(CancellationToken cancellationToken = default)
        {
            if (!_options.Enabled) return;
            var cursor = await _service.GetLatestCursorAsync("", recursive: true, cancellationToken);
            SaveSyncState(cursor, DateTime.UtcNow, lastSyncedUtc: "");
        }

        /// <summary>
        /// Brings the cache up to date by draining account-wide deltas from the
        /// captured sync cursor (<c>list_folder/continue</c>), applying each page's
        /// adds/updates/removes to the matching parent-folder entries and advancing
        /// plus persisting the cursor after every page so an interrupted drain
        /// resumes from the last completed page. When Dropbox signals the cursor is
        /// no longer usable, returns with <see cref="SyncResult.ResetRequired"/> set
        /// so the caller can rebuild.
        /// </summary>
        public async Task<SyncResult> SyncAsync(CancellationToken cancellationToken = default)
        {
            var result = new SyncResult();
            if (!_options.Enabled) return result;

            var state = GetSyncState();
            if (state == null)
                throw new InvalidOperationException(
                    "No sync cursor has been captured. Build the cache first so a cursor is recorded.");

            var cursor = state.Cursor;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var delta = await _service.ListFolderContinueRawAsync(cursor, cancellationToken);
                if (delta.ResetRequired)
                {
                    result.ResetRequired = true;
                    return result;
                }

                foreach (var item in delta.AddsOrUpdates)
                    if (ApplyDeltaAdd(item)) result.Added++;
                foreach (var removed in delta.Removes)
                    if (ApplyDeltaRemove(removed)) result.Removed++;

                cursor = delta.NewCursor;
                result.Pages++;
                PersistSyncPage(cursor, DateTime.UtcNow);
                EvictIfOverBudget();
                if (!delta.HasMore) break;
            }

            return result;
        }

        /// <summary>Applies one delta add/update to the parent folder's cached
        /// entry, hydrating the parent from disk when necessary and ensuring a new
        /// folder item also gets its own (initially empty) entry so its later
        /// children attach. Returns <c>true</c> when the change landed; <c>false</c>
        /// when the parent folder is not cached (nothing to attach to).</summary>
        private bool ApplyDeltaAdd(DropboxItem item)
        {
            var parent = ParentOf(item.Path) ?? "";
            if (!TryGet(parent, out var entry) || entry == null) return false;
            ApplyAddTo(entry, item);
            entry.Dirty = true;
            if (item.IsFolder) GetOrCreateBuildEntry(MakeKey(item.Path), item.Path);
            return true;
        }

        /// <summary>Applies one delta remove (a lowercased path) by dropping the
        /// item from its parent folder's entry and deleting the path's own entry
        /// when it was a cached folder. Returns <c>true</c> when an item was removed
        /// from a parent entry.</summary>
        private bool ApplyDeltaRemove(string loweredPath)
        {
            var changed = false;
            var parent = ParentOf(loweredPath) ?? "";
            if (TryGet(parent, out var entry) && entry != null)
            {
                var before = entry.Items.Count;
                entry.Items.RemoveAll(i => (i.Path ?? "").ToLowerInvariant() == loweredPath);
                if (entry.Items.Count != before)
                {
                    entry.Dirty = true;
                    changed = true;
                }
            }

            var key = MakeKey(loweredPath);
            _entries.TryRemove(key, out _);
            DeleteRow(key);
            return changed;
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
                    ");" +
                    "CREATE TABLE IF NOT EXISTS sync_state (" +
                    "  id INTEGER PRIMARY KEY CHECK (id = 1)," +
                    "  cursor TEXT NOT NULL," +
                    "  captured_utc TEXT NOT NULL," +
                    "  last_synced_utc TEXT NOT NULL DEFAULT ''" +
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

        /// <summary>Loads the keys of every persisted entry at or below
        /// <paramref name="startKey"/>. An empty key selects the whole account.
        /// The subtree query matches the start entry itself plus any descendant
        /// whose key begins with <c>startKey + "/"</c>, with LIKE metacharacters
        /// in the key escaped so paths containing <c>%</c> or <c>_</c> cannot
        /// over-match.</summary>
        private List<string> LoadEntryKeysUnder(string startKey)
        {
            var keys = new List<string>();
            lock (_diskLock)
            {
                using var cmd = _db.CreateCommand();
                if (string.IsNullOrEmpty(startKey))
                {
                    cmd.CommandText = "SELECT path_lower FROM entries;";
                }
                else
                {
                    cmd.CommandText =
                        "SELECT path_lower FROM entries " +
                        "WHERE path_lower = $k OR path_lower LIKE $p ESCAPE '\\';";
                    cmd.Parameters.AddWithValue("$k", startKey);
                    cmd.Parameters.AddWithValue("$p", EscapeLike(startKey) + "/%");
                }

                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    keys.Add(reader.GetString(0));
                }
            }
            return keys;
        }

        /// <summary>Reads and deserializes a single entry's item list from the
        /// database, returning <c>null</c> when the row is absent or corrupt.</summary>
        private List<DropboxItem>? TryLoadEntryItems(string key)
        {
            lock (_diskLock)
            {
                using var cmd = _db.CreateCommand();
                cmd.CommandText = "SELECT items_json FROM entries WHERE path_lower = $k;";
                cmd.Parameters.AddWithValue("$k", key);

                using var reader = cmd.ExecuteReader();
                if (!reader.Read()) return null;

                try
                {
                    return JsonSerializer.Deserialize<List<DropboxItem>>(reader.GetString(0), JsonOpts)
                           ?? new List<DropboxItem>();
                }
                catch
                {
                    return null; // skip corrupt row
                }
            }
        }

        /// <summary>Escapes SQLite LIKE metacharacters (<c>\</c>, <c>%</c>,
        /// <c>_</c>) so a literal subtree key cannot act as a wildcard. Pairs
        /// with <c>ESCAPE '\'</c> in the query.</summary>
        private static string EscapeLike(string value) =>
            value.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

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

        /// <summary>Drops recorded build progress so a subsequent build re-walks the
        /// subtree, or the whole account when <paramref name="path"/> is null. Used
        /// by a rebuild so a previously-completed build is not skipped.</summary>
        public void ClearBuildProgress(string? path = null)
        {
            lock (_diskLock)
            {
                using var cmd = _db.CreateCommand();
                if (path == null)
                {
                    cmd.CommandText = "DELETE FROM build_progress;";
                }
                else
                {
                    cmd.CommandText = "DELETE FROM build_progress WHERE root_path_lower = $k;";
                    cmd.Parameters.AddWithValue("$k",
                        MakeKey(DropboxServiceClient.NormalizePath(path)));
                }
                cmd.ExecuteNonQuery();
            }
        }

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

        /// <summary>Loads the persisted account sync state, or <c>null</c> when no
        /// cursor has been captured.</summary>
        public SyncStateInfo? GetSyncState()
        {
            lock (_diskLock)
            {
                using var cmd = _db.CreateCommand();
                cmd.CommandText =
                    "SELECT cursor, captured_utc, last_synced_utc FROM sync_state WHERE id = 1;";
                using var reader = cmd.ExecuteReader();
                if (!reader.Read()) return null;
                var lastSynced = reader.GetString(2);
                return new SyncStateInfo
                {
                    Cursor = reader.GetString(0),
                    CapturedUtc = ParseUtc(reader.GetString(1)),
                    LastSyncedUtc = string.IsNullOrEmpty(lastSynced) ? null : ParseUtc(lastSynced)
                };
            }
        }

        /// <summary>Inserts or replaces the singleton account sync-state row.</summary>
        private void SaveSyncState(string cursor, DateTime capturedUtc, string lastSyncedUtc)
        {
            lock (_diskLock)
            {
                using var cmd = _db.CreateCommand();
                cmd.CommandText =
                    "INSERT INTO sync_state (id, cursor, captured_utc, last_synced_utc) " +
                    "VALUES (1, $c, $cap, $ls) " +
                    "ON CONFLICT(id) DO UPDATE SET " +
                    "  cursor=excluded.cursor, captured_utc=excluded.captured_utc, " +
                    "  last_synced_utc=excluded.last_synced_utc;";
                cmd.Parameters.AddWithValue("$c", cursor);
                cmd.Parameters.AddWithValue("$cap", FormatUtc(capturedUtc));
                cmd.Parameters.AddWithValue("$ls", lastSyncedUtc);
                cmd.ExecuteNonQuery();
            }
        }

        /// <summary>Flushes the dirty entries mutated while applying one delta page
        /// and advances the persisted sync cursor in a single transaction, so an
        /// interrupted drain resumes from exactly the last completed page.</summary>
        private void PersistSyncPage(string cursor, DateTime lastSyncedUtc)
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

                using var cmd = _db.CreateCommand();
                cmd.CommandText =
                    "UPDATE sync_state SET cursor=$c, last_synced_utc=$ls WHERE id = 1;";
                cmd.Parameters.AddWithValue("$c", cursor);
                cmd.Parameters.AddWithValue("$ls", FormatUtc(lastSyncedUtc));
                cmd.ExecuteNonQuery();
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
