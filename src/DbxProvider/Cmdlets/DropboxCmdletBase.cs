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

        /// <summary>Strips a drive qualifier (e.g. <c>Dbx:</c>) from a path,
        /// leaving the Dropbox-relative path. Shared by the cache finders.</summary>
        protected static string StripDrivePrefix(string path)
        {
            if (string.IsNullOrEmpty(path)) return string.Empty;
            int colon = path.IndexOf(':');
            return colon >= 0 ? path.Substring(colon + 1) : path;
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
            public CmdletRateLimitNotifier(DropboxCmdletBase cmdlet) => _cmdlet = cmdlet;

            public void OnRateLimited(int attempt, TimeSpan retryAfter, TimeSpan totalWaited, string reason)
            {
                int seconds = (int)Math.Ceiling(retryAfter.TotalSeconds);
                int totalSeconds = (int)Math.Ceiling(totalWaited.TotalSeconds);
                _cmdlet.EnqueueWrite(() => _cmdlet.WriteWarning(
                    $"Dropbox returned a transient error ({reason}). Waiting {seconds}s before retry. Press Ctrl+C to cancel."));
                _cmdlet.EnqueueWrite(() => _cmdlet.WriteVerbose(
                    $"Transient retry: attempt #{attempt} failed ({reason}); waiting {seconds}s; cumulative wait so far {totalSeconds}s."));
            }
        }
    }
}
