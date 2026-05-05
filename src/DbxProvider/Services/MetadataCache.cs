using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DbxProvider.Models;

namespace DbxProvider.Services
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
    /// Entries are persisted to JSON under
    /// <c>%LOCALAPPDATA%\DbxProvider\cache\&lt;accountIdHash&gt;</c>; the cache
    /// is hydrated from disk on construction so the warm-path survives
    /// process restarts.
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

        private readonly DropboxServiceClient _service;
        private readonly CacheOptions _options;
        private readonly string _accountId;
        private readonly string _accountIdHash;
        private readonly string _accountDir;
        private readonly ConcurrentDictionary<string, Entry> _entries =
            new(StringComparer.OrdinalIgnoreCase);
        private readonly object _diskLock = new();
        private readonly Timer? _flushTimer;
        private bool _disposed;

        private static readonly JsonSerializerOptions JsonOpts = new()
        {
            WriteIndented = false,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        public MetadataCache(DropboxServiceClient service, string accountId,
            CacheOptions? options = null)
        {
            _service = service ?? throw new ArgumentNullException(nameof(service));
            _accountId = accountId ?? "";
            _options = options ?? CacheOptions.Default;

            _accountIdHash = HashString(_accountId);
            _accountDir = Path.Combine(_options.EffectiveRootDirectory, _accountIdHash);

            HydrateFromDisk();

            if (_options.FlushIntervalSeconds > 0)
            {
                var period = TimeSpan.FromSeconds(_options.FlushIntervalSeconds);
                _flushTimer = new Timer(_ => SafeFlush(), null, period, period);
            }
        }

        public CacheOptions Options => _options;
        public string AccountId => _accountId;
        public string AccountIdHash => _accountIdHash;
        public string AccountDirectory => _accountDir;
        public int Count => _entries.Count;
        public IReadOnlyCollection<Entry> Snapshot() => _entries.Values.ToArray();

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
            EvictIfOverCapacity();
            return entry.Items.ToList();
        }

        /// <summary>Returns the cached entry for <paramref name="path"/> without validating.</summary>
        public bool TryGet(string path, out Entry? entry)
        {
            return _entries.TryGetValue(MakeKey(path), out entry);
        }

        /// <summary>Eagerly run validate-and-merge for a path (or all paths if null).</summary>
        public async Task UpdateAsync(string? path = null, CancellationToken cancellationToken = default)
        {
            if (path != null)
            {
                if (_entries.TryGetValue(MakeKey(path), out var e))
                    await ValidateAsync(e, cancellationToken);
                return;
            }
            foreach (var e in _entries.Values.ToList())
                await ValidateAsync(e, cancellationToken);
        }

        /// <summary>Drop a path's entry (or all entries if null).</summary>
        public void Clear(string? path = null)
        {
            if (path == null)
            {
                _entries.Clear();
                lock (_diskLock)
                {
                    if (Directory.Exists(_accountDir))
                    {
                        try { Directory.Delete(_accountDir, recursive: true); } catch { /* best effort */ }
                    }
                }
                return;
            }

            var key = MakeKey(path);
            _entries.TryRemove(key, out _);
            lock (_diskLock)
            {
                var file = Path.Combine(_accountDir, HashString(key) + ".json");
                if (File.Exists(file)) try { File.Delete(file); } catch { }
            }
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
            lock (_diskLock)
            {
                var file = Path.Combine(_accountDir, HashString(key) + ".json");
                if (File.Exists(file)) try { File.Delete(file); } catch { }
            }
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

        private void EvictIfOverCapacity()
        {
            if (_entries.Count <= _options.MaxEntries) return;
            var victims = _entries.Values
                .OrderBy(e => e.LastUsedUtc)
                .Take(_entries.Count - _options.MaxEntries)
                .ToList();
            foreach (var v in victims)
            {
                _entries.TryRemove(v.PathLower, out _);
                lock (_diskLock)
                {
                    var file = Path.Combine(_accountDir, HashString(v.PathLower) + ".json");
                    if (File.Exists(file)) try { File.Delete(file); } catch { }
                }
            }
        }

        // ----- disk persistence ----------------------------------------------

        private void HydrateFromDisk()
        {
            try
            {
                if (!Directory.Exists(_accountDir)) return;
                foreach (var file in Directory.EnumerateFiles(_accountDir, "*.json"))
                {
                    try
                    {
                        var json = File.ReadAllText(file);
                        var entry = JsonSerializer.Deserialize<Entry>(json, JsonOpts);
                        if (entry == null || string.IsNullOrEmpty(entry.PathLower)) continue;
                        entry.Dirty = false;
                        _entries[entry.PathLower] = entry;
                    }
                    catch { /* skip corrupt files */ }
                }
            }
            catch { /* best effort */ }
        }

        public void Flush()
        {
            lock (_diskLock)
            {
                try { Directory.CreateDirectory(_accountDir); }
                catch { return; }

                foreach (var entry in _entries.Values)
                {
                    if (!entry.Dirty) continue;
                    try
                    {
                        var file = Path.Combine(_accountDir, HashString(entry.PathLower) + ".json");
                        var json = JsonSerializer.Serialize(entry, JsonOpts);
                        File.WriteAllText(file, json);
                        entry.Dirty = false;
                    }
                    catch { /* best effort */ }
                }
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

        private static string HashString(string input)
        {
            using var sha = SHA256.Create();
            var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(input ?? ""));
            return Convert.ToHexString(bytes).ToLowerInvariant();
        }
    }
}
