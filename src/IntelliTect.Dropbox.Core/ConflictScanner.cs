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
        private readonly DropboxServiceClient _service;

        /// <summary>Creates a scanner over the supplied Dropbox service client.</summary>
        public ConflictScanner(DropboxServiceClient service)
        {
            _service = service ?? throw new System.ArgumentNullException(nameof(service));
        }

        /// <summary>
        /// Scans for conflict files, using <paramref name="previousState"/> for an
        /// incremental delta fetch when it is compatible, otherwise doing a full
        /// recursive pass.
        /// </summary>
        /// <param name="parameters">Scan inputs (start path, pattern, size filter).</param>
        /// <param name="previousState">Saved state from a prior run, or null for a cold full scan.</param>
        /// <param name="cancellationToken">Token to cancel the scan.</param>
        /// <param name="onProgress">
        /// Optional callback invoked after every page is applied (both during a
        /// full enumeration and a delta catch-up), receiving the in-progress
        /// state. Callers can persist it so an interrupted scan resumes from the
        /// last saved cursor instead of restarting from scratch.
        /// </param>
        public async Task<ConflictScanResult> ScanAsync(
            ConflictScanParameters parameters,
            ConflictScanState? previousState,
            CancellationToken cancellationToken = default,
            System.Action<ConflictScanState>? onProgress = null)
        {
            if (parameters is null) throw new System.ArgumentNullException(nameof(parameters));

            var account = await _service.GetCurrentAccountAsync(cancellationToken).ConfigureAwait(false);
            var startPath = DropboxServiceClient.NormalizePath(parameters.StartPath);
            var matcher = new WildcardMatcher(parameters.Pattern);

            if (!CanReuse(previousState, parameters, startPath, account.AccountId))
                return await FullScanAsync(parameters, startPath, account.AccountId, matcher, onProgress, cancellationToken).ConfigureAwait(false);

            return await IncrementalScanAsync(parameters, startPath, account.AccountId, matcher, previousState!, onProgress, cancellationToken)
                .ConfigureAwait(false);
        }

        private static bool CanReuse(ConflictScanState? state, ConflictScanParameters p, string startPath, string accountId) =>
            state is not null
            && !string.IsNullOrEmpty(state.Cursor)
            && state.AccountId == accountId
            && state.StartPath == startPath
            && state.Pattern == p.Pattern
            && state.IncludeNonZero == p.IncludeNonZero;
        private async Task<ConflictScanResult> FullScanAsync(
            ConflictScanParameters p, string startPath, string accountId, WildcardMatcher matcher,
            System.Action<ConflictScanState>? onProgress, CancellationToken ct)
        {
            // Stream the recursive listing one page at a time, retaining only the
            // matches (and the cursor) -- never the whole tree. On a large account
            // a single buffered enumeration would materialize millions of items
            // and exhaust memory. Paging also lets the caller persist the cursor
            // between pages so an interrupted full scan resumes via list_folder/
            // continue instead of restarting.
            var state = new ConflictScanState
            {
                AccountId = accountId,
                StartPath = startPath,
                Pattern = p.Pattern,
                IncludeNonZero = p.IncludeNonZero,
            };

            var page = await _service
                .ListFolderFirstPageAsync(startPath, recursive: true, includeDeleted: false, cancellationToken: ct)
                .ConfigureAwait(false);

            foreach (var item in page.Items)
                Upsert(state, item, matcher, p.IncludeNonZero);
            state.Cursor = page.Cursor;
            onProgress?.Invoke(state);

            var hasMore = page.HasMore;
            while (hasMore)
            {
                var delta = await _service.ListFolderContinueRawAsync(state.Cursor, ct).ConfigureAwait(false);
                if (delta.ResetRequired)
                    return await FullScanAsync(p, startPath, accountId, matcher, onProgress, ct).ConfigureAwait(false);

                ApplyDelta(delta, state, matcher, p.IncludeNonZero);
                state.Cursor = delta.NewCursor;
                hasMore = delta.HasMore;
                onProgress?.Invoke(state);
            }

            return new ConflictScanResult(state.Matches.Values.ToList(), state, wasFullScan: true);
        }

        private async Task<ConflictScanResult> IncrementalScanAsync(
            ConflictScanParameters p, string startPath, string accountId, WildcardMatcher matcher,
            ConflictScanState previousState, System.Action<ConflictScanState>? onProgress, CancellationToken ct)
        {
            var state = CloneForUpdate(previousState, accountId, startPath, p);
            var cursor = state.Cursor;

            while (true)
            {
                var delta = await _service.ListFolderContinueRawAsync(cursor, ct).ConfigureAwait(false);
                if (delta.ResetRequired)
                    return await FullScanAsync(p, startPath, accountId, matcher, onProgress, ct).ConfigureAwait(false);

                ApplyDelta(delta, state, matcher, p.IncludeNonZero);
                cursor = delta.NewCursor;
                state.Cursor = cursor;
                onProgress?.Invoke(state);
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