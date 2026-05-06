using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using DbxProvider.Services;
using Xunit;

namespace DbxProvider.UnitTests;

public class RateLimitRetryTests
{
    private sealed class FakeDelay : IDelay
    {
        public List<TimeSpan> Requested { get; } = new();
        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            Requested.Add(delay);
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
    }

    private sealed class CancelDuringDelay : IDelay
    {
        private readonly CancellationTokenSource _cts;
        public CancelDuringDelay(CancellationTokenSource cts) => _cts = cts;
        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            _cts.Cancel();
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingNotifier : IRateLimitNotifier
    {
        public List<(int attempt, TimeSpan retryAfter, TimeSpan total, string reason)> Events { get; } = new();
        public void OnRateLimited(int attempt, TimeSpan retryAfter, TimeSpan totalWaited, string reason)
            => Events.Add((attempt, retryAfter, totalWaited, reason));
    }

    /// <summary>Build a budget that never exhausts so individual cases can isolate
    /// behavior from CI-default budget side-effects when CI=true is set in the env.</summary>
    private static RetryBudget Unbounded() =>
        InvokeForTesting(null);

    private static RetryBudget InvokeForTesting(TimeSpan? maxElapsed)
    {
        var m = typeof(RetryBudget).GetMethod(
            "ForTesting",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
        return (RetryBudget)m!.Invoke(null, new object?[] { maxElapsed })!;
    }

    [Fact]
    public async Task Retries_on_simulated_rate_limit_and_succeeds_on_third_attempt()
    {
        int calls = 0;
        var delay = new FakeDelay();
        var notifier = new RecordingNotifier();

        var result = await RateLimitRetry.ExecuteAsync<string>(
            ct =>
            {
                calls++;
                if (calls < 3) throw new SimulatedRateLimitException(2);
                return Task.FromResult("ok");
            },
            notifier, delay, simulator: null, CancellationToken.None, Unbounded());

        Assert.Equal("ok", result);
        Assert.Equal(3, calls);
        Assert.Equal(2, notifier.Events.Count);
        Assert.Equal(1, notifier.Events[0].attempt);
        Assert.Equal(2, notifier.Events[1].attempt);
        Assert.Equal(TimeSpan.FromSeconds(2), notifier.Events[0].retryAfter);
        Assert.Equal(TimeSpan.FromSeconds(4), notifier.Events[1].total);
        Assert.All(notifier.Events, e => Assert.Contains("429", e.reason));
        Assert.Equal(2, delay.Requested.Count);
        Assert.All(delay.Requested, d => Assert.Equal(TimeSpan.FromSeconds(2), d));
    }

    [Fact]
    public async Task Honors_retry_after_value_from_exception()
    {
        int calls = 0;
        var delay = new FakeDelay();

        await RateLimitRetry.ExecuteAsync<string>(
            ct =>
            {
                calls++;
                if (calls == 1) throw new SimulatedRateLimitException(7);
                return Task.FromResult("ok");
            },
            notifier: null, delay, simulator: null, CancellationToken.None, Unbounded());

        Assert.Single(delay.Requested);
        Assert.Equal(TimeSpan.FromSeconds(7), delay.Requested[0]);
    }

    [Fact]
    public async Task Soft_throttle_uses_exponential_backoff_capped_at_30s()
    {
        int calls = 0;
        var delay = new FakeDelay();
        var notifier = new RecordingNotifier();

        var result = await RateLimitRetry.ExecuteAsync<int>(
            ct =>
            {
                calls++;
                if (calls < 7) throw new SimulatedSoftRateLimitException();
                return Task.FromResult(99);
            },
            notifier, delay, simulator: null, CancellationToken.None, Unbounded());

        Assert.Equal(99, result);
        Assert.Equal(7, calls);
        // Waits should be 1, 2, 4, 8, 16, 30.
        Assert.Equal(6, delay.Requested.Count);
        Assert.Equal(TimeSpan.FromSeconds(1), delay.Requested[0]);
        Assert.Equal(TimeSpan.FromSeconds(2), delay.Requested[1]);
        Assert.Equal(TimeSpan.FromSeconds(4), delay.Requested[2]);
        Assert.Equal(TimeSpan.FromSeconds(8), delay.Requested[3]);
        Assert.Equal(TimeSpan.FromSeconds(16), delay.Requested[4]);
        Assert.Equal(TimeSpan.FromSeconds(30), delay.Requested[5]);
        Assert.All(notifier.Events, e => Assert.Contains("too_many_write_operations", e.reason));
    }

    [Fact]
    public async Task Server_5xx_uses_exponential_backoff()
    {
        int calls = 0;
        var delay = new FakeDelay();
        var notifier = new RecordingNotifier();

        await RateLimitRetry.ExecuteAsync<int>(
            ct =>
            {
                calls++;
                if (calls < 3) throw new SimulatedServerErrorException(503);
                return Task.FromResult(0);
            },
            notifier, delay, simulator: null, CancellationToken.None, Unbounded());

        Assert.Equal(3, calls);
        Assert.Equal(2, delay.Requested.Count);
        Assert.Equal(TimeSpan.FromSeconds(1), delay.Requested[0]);
        Assert.Equal(TimeSpan.FromSeconds(2), delay.Requested[1]);
        Assert.All(notifier.Events, e => Assert.Contains("503", e.reason));
    }

    [Fact]
    public async Task Cancellation_during_delay_throws_OperationCanceled()
    {
        using var cts = new CancellationTokenSource();
        var delay = new CancelDuringDelay(cts);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            RateLimitRetry.ExecuteAsync<string>(
                ct => throw new SimulatedRateLimitException(2),
                notifier: null, delay, simulator: null, cts.Token, Unbounded()));
    }

    [Fact]
    public async Task Cancellation_during_soft_throttle_backoff_throws_OperationCanceled()
    {
        using var cts = new CancellationTokenSource();
        var delay = new CancelDuringDelay(cts);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            RateLimitRetry.ExecuteAsync<string>(
                ct => throw new SimulatedSoftRateLimitException(),
                notifier: null, delay, simulator: null, cts.Token, Unbounded()));
    }

    [Fact]
    public async Task Non_rate_limit_exception_propagates_without_retry()
    {
        int calls = 0;
        var delay = new FakeDelay();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            RateLimitRetry.ExecuteAsync<string>(
                ct =>
                {
                    calls++;
                    throw new InvalidOperationException("boom");
                },
                notifier: null, delay, simulator: null, CancellationToken.None, Unbounded()));

        Assert.Equal(1, calls);
        Assert.Empty(delay.Requested);
    }

    [Fact]
    public async Task HttpRequestException_propagates_without_retry()
    {
        // Connectivity loss is intentionally NOT a throttle; verifies the
        // classifier doesn't accidentally pick it up.
        int calls = 0;
        var delay = new FakeDelay();

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            RateLimitRetry.ExecuteAsync<string>(
                ct => { calls++; throw new HttpRequestException("network down"); },
                notifier: null, delay, simulator: null, CancellationToken.None, Unbounded()));

        Assert.Equal(1, calls);
        Assert.Empty(delay.Requested);
    }

    [Fact]
    public async Task Cancellation_before_call_throws_immediately()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        int calls = 0;
        var delay = new FakeDelay();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            RateLimitRetry.ExecuteAsync<string>(
                ct => { calls++; return Task.FromResult("ok"); },
                notifier: null, delay, simulator: null, cts.Token, Unbounded()));

        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task Simulator_triggers_retry_path()
    {
        EnvironmentRateLimitSimulatorReset(remaining: 2, retryAfterSeconds: 3);
        var sim = new EnvironmentRateLimitSimulator();
        var delay = new FakeDelay();
        int calls = 0;

        var result = await RateLimitRetry.ExecuteAsync<int>(
            ct => { calls++; return Task.FromResult(42); },
            notifier: null, delay, sim, CancellationToken.None, Unbounded());

        Assert.Equal(42, result);
        Assert.Equal(1, calls); // operation only runs after simulator stops throwing
        Assert.Equal(2, delay.Requested.Count);
        Assert.All(delay.Requested, d => Assert.Equal(TimeSpan.FromSeconds(3), d));
    }

    [Fact]
    public async Task Soft_simulator_triggers_retry_path()
    {
        EnvironmentSoftSimulatorReset(2, "too_many_write_operations");
        var sim = new EnvironmentSoftRateLimitSimulator();
        var delay = new FakeDelay();
        int calls = 0;

        var result = await RateLimitRetry.ExecuteAsync<int>(
            ct => { calls++; return Task.FromResult(7); },
            notifier: null, delay, sim, CancellationToken.None, Unbounded());

        Assert.Equal(7, result);
        Assert.Equal(1, calls);
        // Two soft throttles -> 1s, 2s.
        Assert.Equal(new[] { TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2) }, delay.Requested);
    }

    [Fact]
    public async Task Server_simulator_triggers_retry_path()
    {
        EnvironmentServerSimulatorReset(1, 502);
        var sim = new EnvironmentServerErrorSimulator();
        var delay = new FakeDelay();
        int calls = 0;

        await RateLimitRetry.ExecuteAsync<int>(
            ct => { calls++; return Task.FromResult(0); },
            notifier: null, delay, sim, CancellationToken.None, Unbounded());

        Assert.Equal(1, calls);
        Assert.Single(delay.Requested);
        Assert.Equal(TimeSpan.FromSeconds(1), delay.Requested[0]);
    }

    [Fact]
    public async Task Budget_zero_means_no_retry_first_failure_rethrows_wrapped()
    {
        var delay = new FakeDelay();
        var ex = await Assert.ThrowsAsync<RetryBudgetExhaustedException>(() =>
            RateLimitRetry.ExecuteAsync<string>(
                ct => throw new SimulatedRateLimitException(2),
                notifier: null, delay, simulator: null, CancellationToken.None,
                InvokeForTesting(TimeSpan.Zero)));

        Assert.IsType<SimulatedRateLimitException>(ex.InnerException);
        Assert.Empty(delay.Requested);
        Assert.Contains("budget exhausted", ex.Message);
    }

    [Fact]
    public async Task Budget_short_caps_total_wait()
    {
        // 3-second budget, soft throttle would otherwise wait 1+2+4+... .
        // After 1s + 2s = 3s, the next 4s wait would exceed; abort.
        var delay = new FakeDelay();
        var ex = await Assert.ThrowsAsync<RetryBudgetExhaustedException>(() =>
            RateLimitRetry.ExecuteAsync<string>(
                ct => throw new SimulatedSoftRateLimitException(),
                notifier: null, delay, simulator: null, CancellationToken.None,
                InvokeForTesting(TimeSpan.FromSeconds(3))));

        Assert.IsType<SimulatedSoftRateLimitException>(ex.InnerException);
        // Two waits taken (1s, 2s); third (4s) would push past 3s cap.
        Assert.Equal(new[] { TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2) }, delay.Requested);
    }

    [Fact]
    public void Resolve_uses_explicit_env_var_when_set()
    {
        WithEnv(("DBX_RETRY_MAX_ELAPSED_SECONDS", "45"), ("CI", null), ("GITHUB_ACTIONS", null), () =>
        {
            var b = RetryBudget.Resolve();
            Assert.Equal(TimeSpan.FromSeconds(45), b.MaxElapsed);
        });
    }

    [Fact]
    public void Resolve_auto_detects_CI_default_120s()
    {
        WithEnv(("DBX_RETRY_MAX_ELAPSED_SECONDS", null), ("CI", "true"), ("GITHUB_ACTIONS", null), () =>
        {
            var b = RetryBudget.Resolve();
            Assert.Equal(TimeSpan.FromSeconds(RetryBudget.CiDefaultElapsedSeconds), b.MaxElapsed);
        });
    }

    [Fact]
    public void Resolve_unbounded_when_neither_set()
    {
        WithEnv(("DBX_RETRY_MAX_ELAPSED_SECONDS", null), ("CI", null), ("GITHUB_ACTIONS", null), () =>
        {
            var b = RetryBudget.Resolve();
            Assert.Null(b.MaxElapsed);
        });
    }

    [Fact]
    public void Resolve_explicit_zero_disables_retry()
    {
        WithEnv(("DBX_RETRY_MAX_ELAPSED_SECONDS", "0"), ("CI", "true"), ("GITHUB_ACTIONS", null), () =>
        {
            var b = RetryBudget.Resolve();
            Assert.Equal(TimeSpan.Zero, b.MaxElapsed);
        });
    }

    private static void EnvironmentRateLimitSimulatorReset(int remaining, int retryAfterSeconds)
    {
        var m = typeof(EnvironmentRateLimitSimulator).GetMethod(
            "ResetForTests",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
        m!.Invoke(null, new object[] { remaining, retryAfterSeconds });
    }

    private static void EnvironmentSoftSimulatorReset(int remaining, string tag)
    {
        var m = typeof(EnvironmentSoftRateLimitSimulator).GetMethod(
            "ResetForTests",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
        m!.Invoke(null, new object[] { remaining, tag });
    }

    private static void EnvironmentServerSimulatorReset(int remaining, int statusCode)
    {
        var m = typeof(EnvironmentServerErrorSimulator).GetMethod(
            "ResetForTests",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
        m!.Invoke(null, new object[] { remaining, statusCode });
    }

    /// <summary>Set env vars for the duration of <paramref name="body"/>, then restore.</summary>
    private static void WithEnv((string Name, string? Value) e1,
                                (string Name, string? Value) e2,
                                (string Name, string? Value) e3,
                                Action body)
    {
        var saved = new[]
        {
            (e1.Name, Environment.GetEnvironmentVariable(e1.Name)),
            (e2.Name, Environment.GetEnvironmentVariable(e2.Name)),
            (e3.Name, Environment.GetEnvironmentVariable(e3.Name)),
        };
        try
        {
            Environment.SetEnvironmentVariable(e1.Name, e1.Value);
            Environment.SetEnvironmentVariable(e2.Name, e2.Value);
            Environment.SetEnvironmentVariable(e3.Name, e3.Value);
            body();
        }
        finally
        {
            foreach (var (name, value) in saved)
                Environment.SetEnvironmentVariable(name, value);
        }
    }
}
