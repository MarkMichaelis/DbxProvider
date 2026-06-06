using System.Text.Json;
using System.Threading;

namespace IntelliTect.Dropbox
{
    /// <summary>
    /// User-controllable inputs that define which files count as conflicts and
    /// where the scan starts. A change to any of these invalidates a saved
    /// incremental cursor and forces a full re-enumeration.
    /// </summary>
    public sealed class ConflictScanParameters
    {
        /// <summary>Dropbox path the scan starts from. Empty string means the account root.</summary>
        public string StartPath { get; set; } = string.Empty;

        /// <summary>Filename wildcard (PowerShell <c>-like</c> semantics) identifying a conflict file.</summary>
        public string Pattern { get; set; } = "*'s conflicted copy*";

        /// <summary>When true, also capture conflict files that are not zero bytes.</summary>
        public bool IncludeNonZero { get; set; }
    }

    /// <summary>A single conflict file the scan matched.</summary>
    public sealed class ConflictMatch
    {
        /// <summary>Display path of the matched file.</summary>
        public string Path { get; set; } = string.Empty;

        /// <summary>Size of the matched file in bytes.</summary>
        public ulong Bytes { get; set; }
    }

    /// <summary>
    /// Persisted sidecar state carried between scans: the saved recursive
    /// cursor, the parameters it was produced with, and the current match set
    /// keyed by lowercased path (Dropbox delta Removes carry only lowercased
    /// paths).
    /// </summary>
    public sealed class ConflictScanState
    {
        /// <summary>Account the cursor and matches belong to.</summary>
        public string AccountId { get; set; } = string.Empty;

        /// <summary>Normalized StartPath the cursor is scoped to.</summary>
        public string StartPath { get; set; } = string.Empty;

        /// <summary>Conflict pattern the matches were computed with.</summary>
        public string Pattern { get; set; } = string.Empty;

        /// <summary>IncludeNonZero flag the matches were computed with.</summary>
        public bool IncludeNonZero { get; set; }

        /// <summary>Saved account-wide recursive cursor for the next delta fetch.</summary>
        public string Cursor { get; set; } = string.Empty;

        /// <summary>Current matches keyed by lowercased path.</summary>
        public Dictionary<string, ConflictMatch> Matches { get; set; } =
            new(System.StringComparer.Ordinal);

        /// <summary>Serializes this state to JSON for sidecar persistence.</summary>
        public string ToJson() => JsonSerializer.Serialize(this, SerializerOptions);

        /// <summary>Deserializes sidecar state from JSON, or null when the input is blank/invalid.</summary>
        public static ConflictScanState? FromJson(string? json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            try { return JsonSerializer.Deserialize<ConflictScanState>(json!, SerializerOptions); }
            catch (JsonException) { return null; }
        }

        private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };
    }

    /// <summary>Outcome of a single scan run.</summary>
    public sealed class ConflictScanResult
    {
        internal ConflictScanResult(IReadOnlyCollection<ConflictMatch> matches, ConflictScanState state, bool wasFullScan)
        {
            Matches = matches;
            State = state;
            WasFullScan = wasFullScan;
        }

        /// <summary>The conflict files found, in no particular order.</summary>
        public IReadOnlyCollection<ConflictMatch> Matches { get; }

        /// <summary>The state to persist for the next run (cursor + matches + params).</summary>
        public ConflictScanState State { get; }

        /// <summary>True when this run did a full recursive enumeration (cold run, param change, or cursor reset).</summary>
        public bool WasFullScan { get; }
    }

    /// <summary>
    /// Finds zero-byte (or, optionally, all) "conflicted copy" files under a
    /// Dropbox subtree. The first run does a full recursive enumeration and
    /// saves the resulting cursor; subsequent runs fetch only the delta since
    /// that cursor, transparently falling back to a full pass when the cursor
    /// is rejected or the scan parameters change.
    /// </summary>
    public sealed class ConflictScanner
    {
        /// <summary>
        /// Number of results a single search_v2 scope may return before the API
        /// stops yielding more. When a scope reaches this ceiling the scan
        /// subdivides into child folders to stay exhaustive.
        /// </summary>
        private const int DefaultSearchResultCeiling = 10000;

        private readonly DropboxServiceClient _service;
        private readonly int _searchResultCeiling;

        /// <summary>Creates a scanner over the supplied Dropbox service client.</summary>
        public ConflictScanner(DropboxServiceClient service)
            : this(service, DefaultSearchResultCeiling)
        {
        }

        /// <summary>
        /// Creates a scanner with an explicit search ceiling. Used by tests to
        /// exercise the folder-subdivision path without enumerating thousands of
        /// results.
        /// </summary>
        internal ConflictScanner(DropboxServiceClient service, int searchResultCeiling)
        {
            _service = service ?? throw new System.ArgumentNullException(nameof(service));
            _searchResultCeiling = searchResultCeiling;
        }

        /// <summary>
        /// Scans for conflict files, using <paramref name="previousState"/> for an
        /// incremental delta fetch when it is compatible, otherwise doing a full
        /// recursive pass.
        /// </summary>
        public async Task<ConflictScanResult> ScanAsync(
            ConflictScanParameters parameters,
            ConflictScanState? previousState,
            CancellationToken cancellationToken = default)
        {
            if (parameters is null) throw new System.ArgumentNullException(nameof(parameters));

            var account = await _service.GetCurrentAccountAsync(cancellationToken).ConfigureAwait(false);
            var startPath = DropboxServiceClient.NormalizePath(parameters.StartPath);
            var matcher = new WildcardMatcher(parameters.Pattern);

            if (!CanReuse(previousState, parameters, startPath, account.AccountId))
                return await FullScanAsync(parameters, startPath, account.AccountId, matcher, cancellationToken).ConfigureAwait(false);

            return await IncrementalScanAsync(parameters, startPath, account.AccountId, matcher, previousState!, cancellationToken)
                .ConfigureAwait(false);
        }

        /// <summary>
        /// Discovers conflict files using the indexed search_v2 endpoint instead
        /// of a full recursive walk. This is the fast cold-discovery path: it
        /// runs a <c>filenameOnly</c> search derived from
        /// <see cref="ConflictScanParameters.Pattern"/>, post-filters every hit
        /// with the wildcard pattern and the zero-byte rule, and subdivides into
        /// child folders when a scope reaches the search_v2 result ceiling so the
        /// result stays exhaustive. The returned state carries an empty cursor
        /// (search yields no recursive cursor), so the next run searches again.
        /// </summary>
        public async Task<ConflictScanResult> SearchScanAsync(
            ConflictScanParameters parameters, CancellationToken cancellationToken = default)
        {
            if (parameters is null) throw new System.ArgumentNullException(nameof(parameters));

            var account = await _service.GetCurrentAccountAsync(cancellationToken).ConfigureAwait(false);
            var startPath = DropboxServiceClient.NormalizePath(parameters.StartPath);
            var matcher = new WildcardMatcher(parameters.Pattern);
            var query = DeriveSearchQuery(parameters.Pattern);

            var matches = new Dictionary<string, ConflictMatch>(System.StringComparer.Ordinal);
            await SearchScopeAsync(query, startPath, matcher, parameters.IncludeNonZero, matches, cancellationToken)
                .ConfigureAwait(false);

            var state = new ConflictScanState
            {
                AccountId = account.AccountId,
                StartPath = startPath,
                Pattern = parameters.Pattern,
                IncludeNonZero = parameters.IncludeNonZero,
                Cursor = string.Empty,
                Matches = matches,
            };
            return new ConflictScanResult(matches.Values.ToList(), state, wasFullScan: false);
        }

        /// <summary>
        /// Derives a search_v2 token query from a PowerShell wildcard pattern by
        /// splitting on wildcard and separator characters and keeping tokens of
        /// at least two characters (the default pattern yields "conflicted copy").
        /// </summary>
        private static string DeriveSearchQuery(string pattern)
        {
            var tokens = pattern
                .Split(new[] { '*', '?', '[', ']', '/', '\\', ' ', '.', '_', '-', '\'' },
                    System.StringSplitOptions.RemoveEmptyEntries)
                .Where(t => t.Length >= 2);
            return string.Join(" ", tokens);
        }

        /// <summary>
        /// Pages a search scope, collecting matches; when the scope reaches the
        /// ceiling it subdivides into child folders to remain exhaustive.
        /// </summary>
        private async Task SearchScopeAsync(string query, string path, WildcardMatcher matcher,
            bool includeNonZero, Dictionary<string, ConflictMatch> results, CancellationToken ct)
        {
            string? cursor = null;
            int retrieved = 0;
            while (true)
            {
                var page = await _service.SearchFilenamePageAsync(query, path, cursor, ct).ConfigureAwait(false);
                retrieved += CollectMatches(page.Items, matcher, includeNonZero, results);
                cursor = page.Cursor;

                if (retrieved >= _searchResultCeiling)
                {
                    await SubdivideAsync(query, path, matcher, includeNonZero, results, ct).ConfigureAwait(false);
                    return;
                }
                if (!page.HasMore) return;
            }
        }

        /// <summary>Adds satisfying items to <paramref name="results"/>; returns how many were inspected.</summary>
        private static int CollectMatches(IEnumerable<DropboxItem> items, WildcardMatcher matcher,
            bool includeNonZero, Dictionary<string, ConflictMatch> results)
        {
            int count = 0;
            foreach (var item in items)
            {
                count++;
                if (Satisfies(item, matcher, includeNonZero))
                    results[item.Path.ToLowerInvariant()] = new ConflictMatch { Path = item.Path, Bytes = item.Length };
            }
            return count;
        }

        /// <summary>Runs the search independently within each immediate child folder and unions the results.</summary>
        private async Task SubdivideAsync(string query, string path, WildcardMatcher matcher,
            bool includeNonZero, Dictionary<string, ConflictMatch> results, CancellationToken ct)
        {
            var children = await _service.ListFolderAsync(path, recursive: false, includeDeleted: false, ct)
                .ConfigureAwait(false);
            foreach (var child in children.Where(c => c.IsFolder))
                await SearchScopeAsync(query, child.Path, matcher, includeNonZero, results, ct).ConfigureAwait(false);
        }

        private static bool CanReuse(ConflictScanState? state, ConflictScanParameters p, string startPath, string accountId) =>
            state is not null
            && !string.IsNullOrEmpty(state.Cursor)
            && state.AccountId == accountId
            && state.StartPath == startPath
            && state.Pattern == p.Pattern
            && state.IncludeNonZero == p.IncludeNonZero;
        private async Task<ConflictScanResult> FullScanAsync(
            ConflictScanParameters p, string startPath, string accountId, WildcardMatcher matcher, CancellationToken ct)
        {
            var (items, cursor) = await _service
                .ListFolderWithCursorAsync(startPath, recursive: true, includeDeleted: false, ct)
                .ConfigureAwait(false);

            var state = new ConflictScanState
            {
                AccountId = accountId,
                StartPath = startPath,
                Pattern = p.Pattern,
                IncludeNonZero = p.IncludeNonZero,
                Cursor = cursor,
            };

            foreach (var item in items)
            {
                Upsert(state, item, matcher, p.IncludeNonZero);
            }

            return new ConflictScanResult(state.Matches.Values.ToList(), state, wasFullScan: true);
        }

        private async Task<ConflictScanResult> IncrementalScanAsync(
            ConflictScanParameters p, string startPath, string accountId, WildcardMatcher matcher,
            ConflictScanState previousState, CancellationToken ct)
        {
            var state = CloneForUpdate(previousState, accountId, startPath, p);
            var cursor = state.Cursor;

            while (true)
            {
                var delta = await _service.ListFolderContinueRawAsync(cursor, ct).ConfigureAwait(false);
                if (delta.ResetRequired)
                    return await FullScanAsync(p, startPath, accountId, matcher, ct).ConfigureAwait(false);

                ApplyDelta(delta, state, matcher, p.IncludeNonZero);
                cursor = delta.NewCursor;
                state.Cursor = cursor;
                if (!delta.HasMore) break;
            }

            return new ConflictScanResult(state.Matches.Values.ToList(), state, wasFullScan: false);
        }

        private static void ApplyDelta(
            DropboxServiceClient.ListFolderDelta delta, ConflictScanState state, WildcardMatcher matcher, bool includeNonZero)
        {
            foreach (var item in delta.AddsOrUpdates)
            {
                Upsert(state, item, matcher, includeNonZero);
            }

            foreach (var removed in delta.Removes)
                state.Matches.Remove(removed); // Removes are already lowercased
        }

        /// <summary>
        /// Records <paramref name="item"/> as a match when it satisfies the
        /// criteria; otherwise drops any prior match at that path (e.g. a
        /// previously-zero conflict file that grew).
        /// </summary>
        private static void Upsert(ConflictScanState state, DropboxItem item, WildcardMatcher matcher, bool includeNonZero)
        {
            var key = item.Path.ToLowerInvariant();
            if (Satisfies(item, matcher, includeNonZero))
                state.Matches[key] = new ConflictMatch { Path = item.Path, Bytes = item.Length };
            else
                state.Matches.Remove(key);
        }

        private static ConflictScanState CloneForUpdate(
            ConflictScanState previous, string accountId, string startPath, ConflictScanParameters p)
        {
            var state = new ConflictScanState
            {
                AccountId = accountId,
                StartPath = startPath,
                Pattern = p.Pattern,
                IncludeNonZero = p.IncludeNonZero,
                Cursor = previous.Cursor,
            };
            foreach (var kvp in previous.Matches)
                state.Matches[kvp.Key] = new ConflictMatch { Path = kvp.Value.Path, Bytes = kvp.Value.Bytes };
            return state;
        }

        private static bool Satisfies(DropboxItem item, WildcardMatcher matcher, bool includeNonZero) =>
            !item.IsFolder
            && matcher.IsMatch(item.Name)
            && (includeNonZero || item.Length == 0);
    }
}