using System;
using System.Threading;
using System.Threading.Tasks;
using Dropbox.Api;

namespace DbxProvider.Services
{
    /// <summary>
    /// Receives notifications when a Dropbox API call is being retried due
    /// to rate limiting. Implementations marshal the messages to the
    /// PowerShell pipeline thread (<c>WriteWarning</c> / <c>WriteVerbose</c>).
    /// </summary>
    public interface IRateLimitNotifier
    {
        /// <summary>
        /// Called once per rate-limit response, just before the retry
        /// helper waits.
        /// </summary>
        /// <param name="attempt">1-based attempt number that just failed
        /// with a rate-limit response.</param>
        /// <param name="retryAfter">Wait Dropbox asked us to honor.</param>
        /// <param name="totalWaited">Cumulative wait across all rate-limit
        /// retries for this single operation, including this upcoming
        /// wait.</param>
        void OnRateLimited(int attempt, TimeSpan retryAfter, TimeSpan totalWaited);
    }

    /// <summary>
    /// Abstraction over <see cref="Task.Delay(TimeSpan, CancellationToken)"/>
    /// so unit tests can run the retry loop without sleeping.
    /// </summary>
    public interface IDelay
    {
        Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken);
    }

    /// <summary>Default <see cref="IDelay"/> backed by <see cref="Task.Delay(TimeSpan, CancellationToken)"/>.</summary>
    public sealed class SystemDelay : IDelay
    {
        public static readonly SystemDelay Instance = new();
        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken) =>
            Task.Delay(delay, cancellationToken);
    }

    /// <summary>
    /// Optional pluggable hook used by tests / the
    /// <c>DBX_SIMULATE_RATELIMIT</c> environment variable to inject
    /// synthetic rate-limit responses without contacting Dropbox.
    /// </summary>
    public interface IRateLimitSimulator
    {
        /// <summary>
        /// Throws a synthetic <see cref="RateLimitException"/> if the next
        /// invocation should be intercepted; otherwise returns.
        /// </summary>
        void ThrowIfShouldSimulate();
    }

    /// <summary>
    /// Process-wide simulator driven by the <c>DBX_SIMULATE_RATELIMIT</c>
    /// environment variable. Format: <c>count[:seconds]</c>. The first
    /// <c>count</c> calls (across the whole process) throw a synthetic
    /// <see cref="RateLimitException"/> with <c>RetryAfter = seconds</c>
    /// (default 2). Subsequent calls pass through.
    /// </summary>
    public sealed class EnvironmentRateLimitSimulator : IRateLimitSimulator
    {
        public const string EnvVarName = "DBX_SIMULATE_RATELIMIT";
        private static int _remaining;
        private static int _retryAfterSeconds = 2;
        private static string? _lastRaw;
        private static readonly object _initLock = new();

        public void ThrowIfShouldSimulate()
        {
            ReloadIfChanged();
            if (_remaining <= 0) return;

            int next = Interlocked.Decrement(ref _remaining);
            if (next < 0)
            {
                Interlocked.Exchange(ref _remaining, 0);
                return;
            }

            throw new SimulatedRateLimitException(_retryAfterSeconds);
        }

        /// <summary>
        /// Re-reads <see cref="EnvVarName"/> whenever it changes so the
        /// simulator can be (re-)armed mid-process. Setting the variable
        /// to <c>"3:5"</c>, then to <c>"3:5b"</c> (anything different),
        /// re-arms a fresh count of 3.
        /// </summary>
        private static void ReloadIfChanged()
        {
            var raw = Environment.GetEnvironmentVariable(EnvVarName);
            if (string.Equals(raw, _lastRaw, StringComparison.Ordinal)) return;

            lock (_initLock)
            {
                if (string.Equals(raw, _lastRaw, StringComparison.Ordinal)) return;
                _lastRaw = raw;
                _remaining = 0;
                _retryAfterSeconds = 2;

                if (string.IsNullOrWhiteSpace(raw)) return;

                // Strip an optional trailing non-digit token used purely
                // to force a re-arm (e.g. "3:5#a", "3:5b").
                var armSpec = raw.Split('#', 2)[0];
                var parts = armSpec.Split(':');
                if (int.TryParse(parts[0], out var count) && count > 0)
                {
                    _remaining = count;
                    if (parts.Length > 1 && int.TryParse(parts[1], out var seconds) && seconds > 0)
                        _retryAfterSeconds = seconds;
                }
            }
        }

        /// <summary>Reset for tests.</summary>
        internal static void ResetForTests(int remaining, int retryAfterSeconds)
        {
            lock (_initLock)
            {
                // Cache whatever the current env var is so the next
                // ReloadIfChanged is a no-op and our injected counts stick.
                _lastRaw = Environment.GetEnvironmentVariable(EnvVarName);
                _remaining = remaining;
                _retryAfterSeconds = retryAfterSeconds;
            }
        }
    }

    /// <summary>
    /// Executes a Dropbox API call and retries indefinitely while the
    /// server reports rate limiting. Cancellation is honored both during
    /// the call (via cancellation token registration on the task) and during the
    /// inter-attempt wait (via <see cref="IDelay"/>).
    /// </summary>
    public static class RateLimitRetry
    {
        /// <summary>Default wait when the SDK reports a rate-limit retry without a
        /// usable <c>RetryAfter</c> (e.g. <see cref="RetryException"/>).</summary>
        public static readonly TimeSpan DefaultRetryAfter = TimeSpan.FromSeconds(5);

        public static async Task<T> ExecuteAsync<T>(
            Func<CancellationToken, Task<T>> operation,
            IRateLimitNotifier? notifier,
            IDelay? delay,
            IRateLimitSimulator? simulator,
            CancellationToken cancellationToken)
        {
            delay ??= SystemDelay.Instance;
            int attempt = 0;
            var totalWaited = TimeSpan.Zero;

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                attempt++;
                try
                {
                    simulator?.ThrowIfShouldSimulate();
                    return await operation(cancellationToken).WithCancellation(cancellationToken).ConfigureAwait(false);
                }
                catch (RateLimitException ex)
                {
                    var wait = ex.RetryAfter > 0
                        ? TimeSpan.FromSeconds(ex.RetryAfter)
                        : DefaultRetryAfter;
                    totalWaited += wait;
                    notifier?.OnRateLimited(attempt, wait, totalWaited);
                    await delay.DelayAsync(wait, cancellationToken).ConfigureAwait(false);
                }
                catch (SimulatedRateLimitException ex)
                {
                    var wait = ex.RetryAfter > TimeSpan.Zero ? ex.RetryAfter : DefaultRetryAfter;
                    totalWaited += wait;
                    notifier?.OnRateLimited(attempt, wait, totalWaited);
                    await delay.DelayAsync(wait, cancellationToken).ConfigureAwait(false);
                }
            }
        }

        public static async Task ExecuteAsync(
            Func<CancellationToken, Task> operation,
            IRateLimitNotifier? notifier,
            IDelay? delay,
            IRateLimitSimulator? simulator,
            CancellationToken cancellationToken)
        {
            await ExecuteAsync<object?>(async ct => { await operation(ct).ConfigureAwait(false); return null; },
                notifier, delay, simulator, cancellationToken).ConfigureAwait(false);
        }

    }

    /// <summary>
    /// Internal exception used to simulate a Dropbox rate-limit response
    /// without depending on the SDK's non-public
    /// <see cref="RateLimitException"/> constructors. Treated identically
    /// to a real rate-limit response by <see cref="RateLimitRetry"/>.
    /// </summary>
    public sealed class SimulatedRateLimitException : Exception
    {
        public TimeSpan RetryAfter { get; }
        public SimulatedRateLimitException(int retryAfterSeconds)
            : base($"Simulated Dropbox rate limit (retry after {retryAfterSeconds}s).")
        {
            RetryAfter = TimeSpan.FromSeconds(retryAfterSeconds);
        }
        public SimulatedRateLimitException(TimeSpan retryAfter)
            : base($"Simulated Dropbox rate limit (retry after {retryAfter.TotalSeconds:F0}s).")
        {
            RetryAfter = retryAfter;
        }
    }

    internal static class TaskCancellationExtensions
    {
        /// <summary>
        /// Returns a task that completes when <paramref name="task"/>
        /// completes, or throws <see cref="OperationCanceledException"/>
        /// when the token is cancelled. The underlying task is left to
        /// run to completion in the background — this is the only viable
        /// option since the Dropbox SDK does not accept
        /// <see cref="CancellationToken"/>s on its async methods.
        /// </summary>
        public static async Task<T> WithCancellation<T>(this Task<T> task, CancellationToken cancellationToken)
        {
            if (!cancellationToken.CanBeCanceled || task.IsCompleted)
                return await task.ConfigureAwait(false);

            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using (cancellationToken.Register(static s => ((TaskCompletionSource<bool>)s!).TrySetResult(true), tcs))
            {
                var completed = await Task.WhenAny(task, tcs.Task).ConfigureAwait(false);
                if (completed != task)
                    throw new OperationCanceledException(cancellationToken);
            }
            return await task.ConfigureAwait(false);
        }
    }
}
