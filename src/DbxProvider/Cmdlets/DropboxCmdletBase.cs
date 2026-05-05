using System;
using System.Collections.Concurrent;
using System.Management.Automation;
using System.Threading;
using System.Threading.Tasks;
using DbxProvider.Provider;
using DbxProvider.Services;

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

            public void OnRateLimited(int attempt, TimeSpan retryAfter, TimeSpan totalWaited)
            {
                int seconds = (int)Math.Ceiling(retryAfter.TotalSeconds);
                int totalSeconds = (int)Math.Ceiling(totalWaited.TotalSeconds);
                _cmdlet.EnqueueWrite(() => _cmdlet.WriteWarning(
                    $"Dropbox returned 429 (rate limit). Waiting {seconds}s before retry. Press Ctrl+C to cancel."));
                _cmdlet.EnqueueWrite(() => _cmdlet.WriteVerbose(
                    $"Rate-limit retry: attempt #{attempt} failed; waiting {seconds}s; cumulative wait so far {totalSeconds}s."));
            }
        }
    }
}
