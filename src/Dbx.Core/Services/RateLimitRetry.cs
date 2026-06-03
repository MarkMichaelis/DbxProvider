using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Dropbox.Api;

namespace DbxProvider.Services
{
    /// <summary>
    /// Receives notifications when a Dropbox API call is being retried due to
    /// a transient server-side response (rate limit, soft throttle, or 5xx).
    /// Implementations marshal the messages to the PowerShell pipeline thread
    /// (<c>WriteWarning</c> / <c>WriteVerbose</c>).
    /// </summary>
    public interface IRateLimitNotifier
    {
        /// <summary>
        /// Called once per transient response, just before the retry helper
        /// waits.
        /// </summary>
        /// <param name="attempt">1-based attempt number that just failed.</param>
        /// <param name="retryAfter">Wait the helper is about to honor.</param>
        /// <param name="totalWaited">Cumulative wait across all transient
        /// retries for this single operation, including this upcoming
        /// wait.</param>
        /// <param name="reason">Short, user-facing description of why we're
        /// retrying (e.g. <c>"HTTP 429 (gateway rate limit)"</c>,
        /// <c>"too_many_write_operations"</c>, <c>"HTTP 503 (transient server
        /// error)"</c>).</param>
        void OnRateLimited(int attempt, TimeSpan retryAfter, TimeSpan totalWaited, string reason);
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
    /// Optional pluggable hook used by tests / the various
    /// <c>DBX_SIMULATE_*</c> environment variables to inject synthetic
    /// transient responses without contacting Dropbox.
    /// </summary>
    public interface IRateLimitSimulator
    {
        /// <summary>
        /// Throws a synthetic transient exception if the next invocation
        /// should be intercepted; otherwise returns. May be called many
        /// times per process.
        /// </summary>
        void ThrowIfShouldSimulate();
    }

    /// <summary>
    /// Process-wide simulator driven by <c>DBX_SIMULATE_RATELIMIT</c> (HTTP 429
    /// equivalent). Format: <c>count[:seconds]</c>. Re-arms whenever the env
    /// var value changes (append <c>#anything</c> to force a re-arm without
    /// changing the count/seconds, e.g. <c>"3:5"</c> -&gt; <c>"3:5#a"</c>).
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

                var armSpec = raw.Split(new[] { '#' }, 2)[0];
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
                _lastRaw = Environment.GetEnvironmentVariable(EnvVarName);
                _remaining = remaining;
                _retryAfterSeconds = retryAfterSeconds;
            }
        }
    }

    /// <summary>
    /// Process-wide simulator driven by <c>DBX_SIMULATE_SOFT_RATELIMIT</c>
    /// (body-level "soft" throttle equivalent). Format: <c>count[:tag]</c>
    /// where <c>tag</c> defaults to <c>too_many_write_operations</c>.
    /// </summary>
    public sealed class EnvironmentSoftRateLimitSimulator : IRateLimitSimulator
    {
        public const string EnvVarName = "DBX_SIMULATE_SOFT_RATELIMIT";
        private static int _remaining;
        private static string _tag = "too_many_write_operations";
        private static string? _lastRaw;
        private static readonly object _initLock = new();

        public void ThrowIfShouldSimulate()
        {
            ReloadIfChanged();
            if (_remaining <= 0) return;
            int next = Interlocked.Decrement(ref _remaining);
            if (next < 0) { Interlocked.Exchange(ref _remaining, 0); return; }
            throw new SimulatedSoftRateLimitException(_tag);
        }

        private static void ReloadIfChanged()
        {
            var raw = Environment.GetEnvironmentVariable(EnvVarName);
            if (string.Equals(raw, _lastRaw, StringComparison.Ordinal)) return;
            lock (_initLock)
            {
                if (string.Equals(raw, _lastRaw, StringComparison.Ordinal)) return;
                _lastRaw = raw;
                _remaining = 0;
                _tag = "too_many_write_operations";
                if (string.IsNullOrWhiteSpace(raw)) return;

                var armSpec = raw.Split(new[] { '#' }, 2)[0];
                var parts = armSpec.Split(':');
                if (int.TryParse(parts[0], out var count) && count > 0)
                {
                    _remaining = count;
                    if (parts.Length > 1 && !string.IsNullOrWhiteSpace(parts[1]))
                        _tag = parts[1];
                }
            }
        }

        internal static void ResetForTests(int remaining, string tag)
        {
            lock (_initLock)
            {
                _lastRaw = Environment.GetEnvironmentVariable(EnvVarName);
                _remaining = remaining;
                _tag = tag;
            }
        }
    }

    /// <summary>
    /// Process-wide simulator driven by <c>DBX_SIMULATE_SERVER_ERROR</c>
    /// (HTTP 5xx equivalent). Format: <c>count[:status]</c> where
    /// <c>status</c> defaults to <c>503</c>.
    /// </summary>
    public sealed class EnvironmentServerErrorSimulator : IRateLimitSimulator
    {
        public const string EnvVarName = "DBX_SIMULATE_SERVER_ERROR";
        private static int _remaining;
        private static int _statusCode = 503;
        private static string? _lastRaw;
        private static readonly object _initLock = new();

        public void ThrowIfShouldSimulate()
        {
            ReloadIfChanged();
            if (_remaining <= 0) return;
            int next = Interlocked.Decrement(ref _remaining);
            if (next < 0) { Interlocked.Exchange(ref _remaining, 0); return; }
            throw new SimulatedServerErrorException(_statusCode);
        }

        private static void ReloadIfChanged()
        {
            var raw = Environment.GetEnvironmentVariable(EnvVarName);
            if (string.Equals(raw, _lastRaw, StringComparison.Ordinal)) return;
            lock (_initLock)
            {
                if (string.Equals(raw, _lastRaw, StringComparison.Ordinal)) return;
                _lastRaw = raw;
                _remaining = 0;
                _statusCode = 503;
                if (string.IsNullOrWhiteSpace(raw)) return;

                var armSpec = raw.Split(new[] { '#' }, 2)[0];
                var parts = armSpec.Split(':');
                if (int.TryParse(parts[0], out var count) && count > 0)
                {
                    _remaining = count;
                    if (parts.Length > 1 && int.TryParse(parts[1], out var status) && status >= 400)
                        _statusCode = status;
                }
            }
        }

        internal static void ResetForTests(int remaining, int statusCode)
        {
            lock (_initLock)
            {
                _lastRaw = Environment.GetEnvironmentVariable(EnvVarName);
                _remaining = remaining;
                _statusCode = statusCode;
            }
        }
    }

    /// <summary>Fans out to multiple simulators in registration order.</summary>
    public sealed class CompositeRateLimitSimulator : IRateLimitSimulator
    {
        private readonly IRateLimitSimulator[] _inner;
        public CompositeRateLimitSimulator(params IRateLimitSimulator[] inner) => _inner = inner;
        public void ThrowIfShouldSimulate()
        {
            foreach (var s in _inner) s.ThrowIfShouldSimulate();
        }

        /// <summary>Default composite covering all three env-var-driven simulators.</summary>
        public static CompositeRateLimitSimulator Default { get; } = new(
            new EnvironmentRateLimitSimulator(),
            new EnvironmentSoftRateLimitSimulator(),
            new EnvironmentServerErrorSimulator());
    }

    /// <summary>
    /// How a thrown exception should be handled by <see cref="RateLimitRetry"/>.
    /// </summary>
    public enum RetryClassification
    {
        /// <summary>Not transient; rethrow immediately.</summary>
        None = 0,
        /// <summary>HTTP 429 from the gateway. Honor server <c>Retry-After</c>.</summary>
        HardRateLimit,
        /// <summary>Body-level soft throttle (e.g. <c>too_many_write_operations</c>).
        /// Exponential backoff capped at 30 s.</summary>
        SoftThrottle,
        /// <summary>HTTP 5xx / 408. Exponential backoff capped at 30 s.</summary>
        TransientServer,
    }

    /// <summary>Classifies an exception against Dropbox's transient-error surfaces.</summary>
    public static class RetryClassifier
    {
        private static readonly Regex SoftThrottleRegex = new(
            @"too_many_write_operations|too_many_files|too_many_requests|rate_limit",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public static (RetryClassification Kind, string Reason) Classify(Exception ex)
        {
            switch (ex)
            {
                case RateLimitException:
                    return (RetryClassification.HardRateLimit, "HTTP 429 (gateway rate limit)");
                case SimulatedRateLimitException:
                    return (RetryClassification.HardRateLimit, "HTTP 429 (gateway rate limit, simulated)");
                case SimulatedSoftRateLimitException sse:
                    return (RetryClassification.SoftThrottle, sse.Tag + " (simulated)");
                case SimulatedServerErrorException sve:
                    return (RetryClassification.TransientServer, $"HTTP {sve.StatusCode} (transient server error, simulated)");
                case HttpException he when IsRetryableStatus(he.StatusCode):
                    return (RetryClassification.TransientServer, $"HTTP {he.StatusCode} (transient server error)");
            }

            // ApiException<T> is generic; type test via open generic.
            var type = ex.GetType();
            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(ApiException<>))
            {
                if (TryClassifyApiException(ex, out var tag))
                    return (RetryClassification.SoftThrottle, tag);
            }

            return (RetryClassification.None, string.Empty);
        }

        private static bool IsRetryableStatus(int s) =>
            s == 408 || s == 500 || s == 502 || s == 503 || s == 504;

        private static bool TryClassifyApiException(Exception ex, out string tag)
        {
            tag = "soft rate limit";
            // Primary: regex on full ToString() (covers any TError).
            var s = ex.ToString();
            var m = SoftThrottleRegex.Match(s);
            if (m.Success)
            {
                tag = m.Value.ToLowerInvariant();
                return true;
            }

            // Fallback: typed reflection on ErrorResponse looking for IsTooMany* / Is*RateLimit*.
            var errProp = ex.GetType().GetProperty("ErrorResponse");
            var errVal = errProp?.GetValue(ex);
            if (errVal == null) return false;

            foreach (var p in errVal.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (p.PropertyType != typeof(bool)) continue;
                var name = p.Name;
                bool soundsLikeThrottle =
                    name.StartsWith("IsTooMany", StringComparison.Ordinal) ||
                    (name.StartsWith("Is", StringComparison.Ordinal) &&
                     name.IndexOf("RateLimit", StringComparison.OrdinalIgnoreCase) > 0);
                if (!soundsLikeThrottle) continue;

                bool value;
                try { value = (bool)p.GetValue(errVal)!; }
                catch { continue; }
                if (!value) continue;

                tag = name.Length > 2 ? name.Substring(2) : name;
                return true;
            }
            return false;
        }
    }

    /// <summary>
    /// Per-call retry budget. Caps cumulative wait time and per-class attempt
    /// counts so a transient retry storm cannot run indefinitely (especially
    /// in CI). See <see cref="Resolve"/> for env-var precedence.
    /// </summary>
    public sealed class RetryBudget
    {
        /// <summary>Default elapsed cap applied when CI is auto-detected.</summary>
        public const int CiDefaultElapsedSeconds = 120;

        /// <summary>Per-class attempt cap when no elapsed budget is in effect.</summary>
        public const int PerClassAttemptCap = 1000;

        /// <summary>Wall-clock cap on cumulative retry waits, or <c>null</c>
        /// for unbounded (interactive) operation.</summary>
        public TimeSpan? MaxElapsed { get; }

        private readonly Stopwatch _sw = new();
        private readonly Dictionary<RetryClassification, int> _attempts = new();

        private RetryBudget(TimeSpan? maxElapsed) { MaxElapsed = maxElapsed; }

        /// <summary>
        /// Resolve a budget from the environment, in precedence order:
        /// <list type="number">
        ///   <item><c>DBX_RETRY_MAX_ELAPSED_SECONDS</c> if set to an
        ///     integer &gt;= 0 (0 = no retry).</item>
        ///   <item><c>CI=true</c> or <c>GITHUB_ACTIONS=true</c> -&gt;
        ///     default 120 s.</item>
        ///   <item>Otherwise unbounded (with per-class attempt cap as
        ///     a safety net).</item>
        /// </list>
        /// </summary>
        public static RetryBudget Resolve()
        {
            var raw = Environment.GetEnvironmentVariable("DBX_RETRY_MAX_ELAPSED_SECONDS");
            if (!string.IsNullOrWhiteSpace(raw) && int.TryParse(raw, out var s) && s >= 0)
                return new RetryBudget(TimeSpan.FromSeconds(s));
            if (IsCi())
                return new RetryBudget(TimeSpan.FromSeconds(CiDefaultElapsedSeconds));
            return new RetryBudget(null);
        }

        /// <summary>For tests: build a budget directly without consulting env vars.</summary>
        internal static RetryBudget ForTesting(TimeSpan? maxElapsed) => new(maxElapsed);

        private static bool IsCi() =>
            string.Equals(Environment.GetEnvironmentVariable("CI"), "true", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(Environment.GetEnvironmentVariable("GITHUB_ACTIONS"), "true", StringComparison.OrdinalIgnoreCase);

        public void RecordAttempt(RetryClassification kind)
        {
            if (!_sw.IsRunning) _sw.Start();
            _attempts.TryGetValue(kind, out var n);
            _attempts[kind] = n + 1;
        }

        public int AttemptCount(RetryClassification kind) =>
            _attempts.TryGetValue(kind, out var n) ? n : 0;

        public TimeSpan Elapsed => _sw.Elapsed;

        /// <summary>
        /// Returns <c>true</c> if a wait of <paramref name="wait"/> may be
        /// taken; <c>false</c> if doing so would exceed the budget. When
        /// <c>false</c>, <paramref name="exhaustionReason"/> describes which
        /// cap was hit.
        /// </summary>
        public bool TryConsumeWait(TimeSpan wait, out string? exhaustionReason)
        {
            exhaustionReason = null;
            if (MaxElapsed is { } cap)
            {
                if (cap == TimeSpan.Zero || _sw.Elapsed + wait > cap)
                {
                    exhaustionReason =
                        $"retry budget exhausted (cap {cap.TotalSeconds:F0}s, " +
                        $"elapsed {_sw.Elapsed.TotalSeconds:F1}s, next wait {wait.TotalSeconds:F1}s)";
                    return false;
                }
                return true;
            }

            // No elapsed cap: defend against a pathological loop where every
            // wait collapses to ~0 s by capping per-class attempts.
            foreach (var kv in _attempts)
            {
                if (kv.Value > PerClassAttemptCap)
                {
                    exhaustionReason =
                        $"per-class attempt cap exhausted ({kv.Key} = {kv.Value} attempts)";
                    return false;
                }
            }
            return true;
        }
    }

    /// <summary>
    /// Thrown when the per-call <see cref="RetryBudget"/> is exhausted. The
    /// last classified exception is preserved as <see cref="Exception.InnerException"/>
    /// so callers see the underlying Dropbox error.
    /// </summary>
    public sealed class RetryBudgetExhaustedException : Exception
    {
        public RetryBudgetExhaustedException(string reason, Exception inner)
            : base(BuildMessage(reason, inner), inner)
        {
        }

        private static string BuildMessage(string reason, Exception inner) =>
            $"Dropbox retry budget exhausted: {reason}. Last error: {inner.GetType().Name}: {inner.Message}";
    }

    /// <summary>
    /// Executes a Dropbox API call and retries on classified transient
    /// failures (HTTP 429, body-level soft throttles, HTTP 5xx). Cancellation
    /// is honored both during the call and during the inter-attempt wait.
    /// A <see cref="RetryBudget"/> bounds total wait time so retries cannot
    /// loop indefinitely in CI.
    /// </summary>
    public static class RateLimitRetry
    {
        /// <summary>Default wait when a hard rate limit is reported without a
        /// usable <c>Retry-After</c>.</summary>
        public static readonly TimeSpan DefaultRetryAfter = TimeSpan.FromSeconds(5);

        /// <summary>Cap on the per-attempt wait for soft / server backoff.</summary>
        public static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(30);

        public static Task<T> ExecuteAsync<T>(
            Func<CancellationToken, Task<T>> operation,
            IRateLimitNotifier? notifier,
            IDelay? delay,
            IRateLimitSimulator? simulator,
            CancellationToken cancellationToken)
            => ExecuteAsync(operation, notifier, delay, simulator, cancellationToken, budget: null);

        public static async Task<T> ExecuteAsync<T>(
            Func<CancellationToken, Task<T>> operation,
            IRateLimitNotifier? notifier,
            IDelay? delay,
            IRateLimitSimulator? simulator,
            CancellationToken cancellationToken,
            RetryBudget? budget)
        {
            delay ??= SystemDelay.Instance;
            budget ??= RetryBudget.Resolve();
            int attempt = 0;
            var totalWaited = TimeSpan.Zero;

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                attempt++;

                Exception caught;
                try
                {
                    simulator?.ThrowIfShouldSimulate();
                    return await operation(cancellationToken).WithCancellation(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    caught = ex;
                }

                var (kind, reason) = RetryClassifier.Classify(caught);
                if (kind == RetryClassification.None)
                {
                    ExceptionDispatchInfo.Capture(caught).Throw();
                }

                budget.RecordAttempt(kind);
                var wait = ComputeWait(kind, caught, budget.AttemptCount(kind));

                if (!budget.TryConsumeWait(wait, out var exhaustionReason))
                {
                    throw new RetryBudgetExhaustedException(exhaustionReason!, caught);
                }

                totalWaited += wait;
                notifier?.OnRateLimited(attempt, wait, totalWaited, reason);
                await delay.DelayAsync(wait, cancellationToken).ConfigureAwait(false);
            }
        }

        public static async Task ExecuteAsync(
            Func<CancellationToken, Task> operation,
            IRateLimitNotifier? notifier,
            IDelay? delay,
            IRateLimitSimulator? simulator,
            CancellationToken cancellationToken)
        {
            await ExecuteAsync<object?>(
                async ct => { await operation(ct).ConfigureAwait(false); return null; },
                notifier, delay, simulator, cancellationToken).ConfigureAwait(false);
        }

        private static TimeSpan ComputeWait(RetryClassification kind, Exception ex, int classAttempt)
        {
            switch (kind)
            {
                case RetryClassification.HardRateLimit:
                    int seconds = ex switch
                    {
                        RateLimitException rl => rl.RetryAfter,
                        SimulatedRateLimitException srl => (int)Math.Ceiling(srl.RetryAfter.TotalSeconds),
                        _ => 0,
                    };
                    return seconds > 0 ? TimeSpan.FromSeconds(seconds) : DefaultRetryAfter;

                case RetryClassification.SoftThrottle:
                case RetryClassification.TransientServer:
                    // 1, 2, 4, 8, 16, 30, 30, ... seconds.
                    int n = Math.Min(Math.Max(classAttempt, 1), 16); // guard shift overflow
                    double secs = Math.Pow(2, n - 1);
                    return TimeSpan.FromSeconds(Math.Min(secs, MaxBackoff.TotalSeconds));

                default:
                    return TimeSpan.Zero;
            }
        }
    }

    /// <summary>
    /// Internal exception used to simulate a Dropbox HTTP-429 rate-limit
    /// response without depending on the SDK's non-public
    /// <see cref="RateLimitException"/> constructors.
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

    /// <summary>
    /// Internal exception used to simulate a body-level "soft" throttle
    /// (e.g. <c>too_many_write_operations</c>). Treated by
    /// <see cref="RetryClassifier"/> as <see cref="RetryClassification.SoftThrottle"/>.
    /// </summary>
    public sealed class SimulatedSoftRateLimitException : Exception
    {
        public string Tag { get; }
        public SimulatedSoftRateLimitException(string tag = "too_many_write_operations")
            : base($"Simulated Dropbox soft throttle ({tag}).")
        {
            Tag = tag;
        }
    }

    /// <summary>
    /// Internal exception used to simulate a transient HTTP 5xx / 408 response.
    /// </summary>
    public sealed class SimulatedServerErrorException : Exception
    {
        public int StatusCode { get; }
        public SimulatedServerErrorException(int statusCode = 503)
            : base($"Simulated Dropbox HTTP {statusCode} response.")
        {
            StatusCode = statusCode;
        }
    }

    internal static class TaskCancellationExtensions
    {
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
