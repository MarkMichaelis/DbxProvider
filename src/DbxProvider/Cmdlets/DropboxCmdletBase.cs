using System;
using System.Collections.Concurrent;
using System.Management.Automation;
using System.Threading;
using System.Threading.Tasks;
using DbxProvider.Provider;
using IntelliTect.Dropbox;

namespace DbxProvider.Cmdlets
{
    /// <summary>
    /// Base class for cmdlets that need a Dropbox service client.
    ///
    /// Owns a per-invocation <see cref="CancellationTokenSource"/>, wires
    /// it to <see cref="StopProcessing"/>, and exposes a
    /// <see cref="Run{T}"/> helper that runs a Dropbox API call from a
    /// synchronous PowerShell context. The helper:
    /// <list type="bullet">
    /// <item>Provides the cmdlet's <see cref="CancellationToken"/> to the
    ///   service call so rate-limit waits and (best-effort) in-flight
    ///   calls can be cancelled when the user presses Ctrl+C.</item>
    /// <item>Pumps any <c>WriteWarning</c> / <c>WriteVerbose</c> messages
    ///   queued by the rate-limit notifier on the pipeline thread, so
    ///   PowerShell sees them in real time during long waits.</item>
    /// </list>
    /// </summary>
    public abstract class DropboxCmdletBase : PSCmdlet, IDisposable
    {
        [Parameter]
        public string DriveName { get; set; } = "Dbx";

        private CancellationTokenSource? _cts;
        private readonly BlockingCollection<Action> _pendingWrites = new(new ConcurrentQueue<Action>());

        /// <summary>Cancellation token for this cmdlet's current invocation.</summary>
        protected CancellationToken CancellationToken => _cts?.Token ?? CancellationToken.None;

        protected override void BeginProcessing()
        {
            _cts ??= new CancellationTokenSource();
            base.BeginProcessing();
        }

        protected override void StopProcessing()
        {
            try { _cts?.Cancel(); } catch { /* best effort */ }
            base.StopProcessing();
        }

        protected override void EndProcessing()
        {
            try { base.EndProcessing(); }
            finally { Dispose(); }
        }

        /// <summary>Resolves the Dropbox service client for this cmdlet's drive
        /// and registers a notifier so rate-limit waits surface as
        /// <c>WriteWarning</c>/<c>WriteVerbose</c>.</summary>
        protected DropboxServiceClient GetService()
        {
            var drive = SessionState.Drive.Get(DriveName);
            if (drive is DropboxDriveInfo dbxDrive)
            {
                dbxDrive.Service.SetRateLimitNotifier(new CmdletRateLimitNotifier(this));
                return dbxDrive.Service;
            }

            throw new InvalidOperationException(
                $"Drive '{DriveName}:' is not a Dropbox drive. Use Connect-Dropbox first.");
        }

        /// <summary>Strips a leading drive qualifier (e.g. <c>Dbx:</c>) from a
        /// path, leaving the Dropbox-relative path. Only a qualifier at the very
        /// start is removed: if a path separator appears before the colon (for
        /// example <c>/Project:Notes</c>), the colon belongs to the path and the
        /// value is returned unchanged. Shared by the cache finders.</summary>
        internal static string StripDrivePrefix(string path)
        {
            if (string.IsNullOrEmpty(path)) return string.Empty;

            int colon = path.IndexOf(':');
            if (colon < 0) return path;

            int separator = path.IndexOfAny(new[] { '/', '\\' });
            if (separator >= 0 && separator < colon) return path;

            return path.Substring(colon + 1);
        }

        /// <summary>
        /// Emits a Dropbox item to the pipeline with its <c>Path</c> rewritten to a
        /// drive-qualified provider path (e.g. <c>Dbx:\Folder\file</c>) so the
        /// object pipes straight into provider-aware cmdlets such as
        /// <c>Remove-Item</c>, <c>Move-Item</c> and <c>Get-Item</c> from any current
        /// location -- mirroring how the FileSystem provider's items carry a
        /// resolvable path. <c>Remove-Item -Path</c> binds an object's <c>Path</c>
        /// property ahead of <c>PSPath</c>, so a bare API path (<c>/Folder/file</c>)
        /// would otherwise be rooted against the current PSDrive. The raw Dropbox API
        /// path is preserved on the <c>DropboxPath</c> note property (and via the
        /// unchanged <see cref="DropboxItem.FullName"/>).
        /// </summary>
        protected void WriteDropboxItem(DropboxItem item)
        {
            if (item == null) throw new ArgumentNullException(nameof(item));

            var pso = PSObject.AsPSObject(item);
            // Preserve the raw API path before shadowing Path so callers that need
            // the Dropbox-relative form still have it.
            pso.Properties.Add(new PSNoteProperty("DropboxPath", item.Path));
            // Shadow the adapted CLR Path with the drive-qualified, Remove-Item-able
            // value. A PSNoteProperty with the same name takes precedence over the
            // adapted member for both member access and pipeline binding.
            pso.Properties.Add(new PSNoteProperty("Path", ToDriveQualifiedPath(item.Path)));
            WriteObject(pso);
        }

        /// <summary>Converts a Dropbox API path (<c>/Folder/file</c>) to a
        /// drive-qualified provider path for this cmdlet's drive
        /// (<c>Dbx:\Folder\file</c>). The inverse of <see cref="StripDrivePrefix"/>.</summary>
        internal string ToDriveQualifiedPath(string apiPath) =>
            DriveName + ":" + (apiPath ?? string.Empty).Replace('/', '\\');

        /// <summary>Reports whether a query string contains a PowerShell wildcard
        /// metacharacter (<c>*</c>, <c>?</c> or <c>[</c>). Cache-backed finders use
        /// this to auto-detect intent: a query with a wildcard is matched as a glob,
        /// otherwise it is treated as a substring search.</summary>
        internal static bool ContainsWildcard(string? value)
            => !string.IsNullOrEmpty(value) && value.IndexOfAny(new[] { '*', '?', '[' }) >= 0;

        /// <summary>Converts a raw query into a name wildcard pattern for the cache
        /// finders: a query that already contains a wildcard is used verbatim; a
        /// plain query is wrapped as a <c>*query*</c> substring match. A blank query
        /// matches everything.</summary>
        internal static string ToNamePattern(string? query)
        {
            if (string.IsNullOrEmpty(query)) return "*";
            return ContainsWildcard(query) ? query! : $"*{query}*";
        }

        /// <summary>Builds the shared name/zero-byte predicate used by the cache
        /// finders (<c>Search-Dropbox</c> and <c>Find-DropboxConflict</c>): a
        /// wildcard match on the item name and, when <paramref name="zeroByteOnly"/>
        /// is set, a restriction to zero-byte files (folders and non-empty files are
        /// excluded).</summary>
        internal static Func<DropboxItem, bool> BuildNamePredicate(string namePattern, bool zeroByteOnly)
        {
            // A blank or whitespace pattern means "no filter", so treat it the same
            // as '*' rather than an empty literal that matches only empty filenames.
            var pattern = string.IsNullOrWhiteSpace(namePattern) ? "*" : namePattern;
            var matcher = new WildcardMatcher(pattern);
            return item =>
                matcher.IsMatch(item.Name)
                && (!zeroByteOnly || (!item.IsFolder && item.Length == 0));
        }

        private const int RefreshProgressId = 1900;

        /// <summary>
        /// Returns the drive's metadata cache after bringing it up to date from
        /// the account delta cursor -- the shared, cross-cutting refresh every
        /// cache-backed cmdlet runs before reading. While draining it shows a
        /// transient <see cref="Cmdlet.WriteProgress(ProgressRecord)"/> bar (so it
        /// auto-clears) and, on completion, reports the added/removed counts. When
        /// the cache has never captured a sync cursor (an older cache) it captures
        /// a baseline and skips the drain; when Dropbox rejects the cursor it warns
        /// the user to rebuild. Honors Ctrl+C via <see cref="Run(Func{CancellationToken, Task})"/>.
        /// </summary>
        protected MetadataCache GetRefreshedCache()
        {
            // Wire the rate-limit notifier so throttling during the drain surfaces
            // as warnings on the pipeline thread.
            GetService();
            var cache = CacheCmdletHelpers.GetCache(this, DriveName);
            if (!cache.Options.Enabled) return cache;

            if (cache.GetSyncState() == null)
            {
                // Older cache with no delta anchor: capture a baseline now so the
                // next read can drain, but do not enumerate this time.
                Run(ct => cache.EnsureSyncCursorAsync(ct));
                WriteVerbose(
                    "No delta cursor was present; captured a baseline for future incremental " +
                    "refreshes.");
                return cache;
            }

            var progress = new ProgressRecord(RefreshProgressId,
                "Refreshing Dropbox metadata cache", "Draining changes since the last sync...");
            WriteProgress(progress);

            MetadataCache.SyncResult sync;
            try
            {
                sync = Run(ct => cache.SyncAsync(ct));
            }
            finally
            {
                // Always clear the transient progress bar -- even if the drain
                // throws (network or SQLite error) -- so the host is never left
                // with a stuck "Refreshing..." indicator.
                progress.RecordType = ProgressRecordType.Completed;
                WriteProgress(progress);
            }

            if (sync.ResetRequired)
            {
                WriteWarning(
                    "Dropbox rejected the saved delta cursor, so the cache could not be refreshed " +
                    "incrementally. Run 'Build-DropboxCacheAll.ps1 -Rebuild' for a clean baseline.");
                return cache;
            }

            var summary = $"Refreshed cache: {sync.Added} added, {sync.Removed} removed.";
            WriteVerbose(summary);
            return cache;
        }

        /// <summary>
        /// Runs an async Dropbox call from synchronous cmdlet code while
        /// pumping queued <c>WriteWarning</c>/<c>WriteVerbose</c> messages
        /// on the pipeline thread. Throws
        /// <see cref="PipelineStoppedException"/> on cancel.
        /// </summary>
        protected T Run<T>(Func<CancellationToken, Task<T>> op)
        {
            if (op == null) throw new ArgumentNullException(nameof(op));
            _cts ??= new CancellationTokenSource();
            var task = op(_cts.Token);
            PumpUntil(task);
            try { return task.GetAwaiter().GetResult(); }
            catch (OperationCanceledException) { throw new PipelineStoppedException(); }
        }

        /// <summary>Void overload of <see cref="Run{T}"/>.</summary>
        protected void Run(Func<CancellationToken, Task> op)
        {
            if (op == null) throw new ArgumentNullException(nameof(op));
            _cts ??= new CancellationTokenSource();
            var task = op(_cts.Token);
            PumpUntil(task);
            try { task.GetAwaiter().GetResult(); }
            catch (OperationCanceledException) { throw new PipelineStoppedException(); }
        }

        private void PumpUntil(Task task)
        {
            // Drain any queued write actions while the task runs. We poll
            // both the queue and the task on a tight schedule so the user
            // sees messages within ~50ms.
            while (!task.IsCompleted)
            {
                if (_pendingWrites.TryTake(out var action, millisecondsTimeout: 50))
                {
                    try { action(); } catch { /* best-effort UI write */ }
                }
            }
            // Drain any remaining queued messages before returning.
            while (_pendingWrites.TryTake(out var action))
            {
                try { action(); } catch { }
            }
        }

        /// <summary>Internal: enqueues a UI action to run on the pipeline thread.</summary>
        internal void EnqueueWrite(Action action) => _pendingWrites.TryAdd(action);

        public void Dispose()
        {
            try { _cts?.Cancel(); } catch { }
            try { _cts?.Dispose(); } catch { }
            _cts = null;
            try { _pendingWrites.Dispose(); } catch { }
            GC.SuppressFinalize(this);
        }

        private sealed class CmdletRateLimitNotifier : IRateLimitNotifier
        {
            private readonly DropboxCmdletBase _cmdlet;
            private readonly TransientRetryThrottle _throttle = new();
            public CmdletRateLimitNotifier(DropboxCmdletBase cmdlet) => _cmdlet = cmdlet;

            public void OnRateLimited(int attempt, TimeSpan retryAfter, TimeSpan totalWaited, string reason)
            {
                int seconds = (int)Math.Ceiling(retryAfter.TotalSeconds);
                int totalSeconds = (int)Math.Ceiling(totalWaited.TotalSeconds);

                // Transient throttling (too_many_write_operations) is routine during
                // a large concurrent batch delete and is retried automatically with
                // backoff -- no user action is needed. Because it needs no action, it
                // is never surfaced as a warning; it is only emitted under -Verbose for
                // diagnostics. A throttled heartbeat keeps even the verbose stream from
                // being buried under one line per retry.
                if (_throttle.ShouldWarn())
                {
                    int suppressed = _throttle.SuppressedSinceLastWarn;
                    string extra = suppressed > 0
                        ? $" ({suppressed:N0} similar retries since the last notice)"
                        : string.Empty;
                    _cmdlet.EnqueueWrite(() => _cmdlet.WriteVerbose(
                        $"Dropbox is throttling writes ({reason}); auto-retrying with backoff -- " +
                        $"this is normal and needs no action.{extra}"));
                }
                else
                {
                    _cmdlet.EnqueueWrite(() => _cmdlet.WriteVerbose(
                        $"Transient retry: attempt #{attempt} failed ({reason}); waiting {seconds}s; cumulative wait so far {totalSeconds}s."));
                }
            }
        }
    }

    /// <summary>
    /// Decides how often the relentless, auto-retried transient-throttle notices
    /// (<c>too_many_write_operations</c>) should be surfaced as a friendly
    /// heartbeat versus a terse per-attempt diagnostic. Both are emitted only under
    /// <c>-Verbose</c> (the retries need no user action, so they are never warnings);
    /// the heartbeat fires on the first occurrence and then once every
    /// <see cref="WarnEvery"/> occurrences so a long concurrent batch delete does not
    /// bury the verbose stream under a wall of identical lines.
    /// </summary>
    internal sealed class TransientRetryThrottle
    {
        /// <summary>Emit a heartbeat notice on the first occurrence and then once
        /// per this many occurrences; all others fall through to the terse form.</summary>
        internal const int WarnEvery = 25;

        private int _count;
        private int _lastWarnedAt;

        /// <summary>Number of occurrences since the last heartbeat, so the next
        /// heartbeat can report how many retries it stands in for.</summary>
        public int SuppressedSinceLastWarn { get; private set; }

        /// <summary>Records one transient retry and returns whether it should be
        /// surfaced as a heartbeat (true on the 1st, 26th, 51st, ... occurrence).</summary>
        public bool ShouldWarn()
        {
            int n = ++_count;
            bool warn = n == 1 || (n - _lastWarnedAt) >= WarnEvery;
            if (warn)
            {
                SuppressedSinceLastWarn = n - _lastWarnedAt - 1;
                _lastWarnedAt = n;
            }
            return warn;
        }
    }
}
