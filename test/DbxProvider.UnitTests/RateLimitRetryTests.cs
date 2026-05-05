using System;
using System.Collections.Generic;
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
        public List<(int attempt, TimeSpan retryAfter, TimeSpan total)> Events { get; } = new();
        public void OnRateLimited(int attempt, TimeSpan retryAfter, TimeSpan totalWaited)
            => Events.Add((attempt, retryAfter, totalWaited));
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
            notifier, delay, simulator: null, CancellationToken.None);

        Assert.Equal("ok", result);
        Assert.Equal(3, calls);
        Assert.Equal(2, notifier.Events.Count);
        Assert.Equal(1, notifier.Events[0].attempt);
        Assert.Equal(2, notifier.Events[1].attempt);
        Assert.Equal(TimeSpan.FromSeconds(2), notifier.Events[0].retryAfter);
        Assert.Equal(TimeSpan.FromSeconds(4), notifier.Events[1].total);
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
            notifier: null, delay, simulator: null, CancellationToken.None);

        Assert.Single(delay.Requested);
        Assert.Equal(TimeSpan.FromSeconds(7), delay.Requested[0]);
    }

    [Fact]
    public async Task Cancellation_during_delay_throws_OperationCanceled()
    {
        using var cts = new CancellationTokenSource();
        var delay = new CancelDuringDelay(cts);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            RateLimitRetry.ExecuteAsync<string>(
                ct => throw new SimulatedRateLimitException(2),
                notifier: null, delay, simulator: null, cts.Token));
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
                notifier: null, delay, simulator: null, CancellationToken.None));

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
                notifier: null, delay, simulator: null, cts.Token));

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
            notifier: null, delay, sim, CancellationToken.None);

        Assert.Equal(42, result);
        Assert.Equal(1, calls); // operation only runs after simulator stops throwing
        Assert.Equal(2, delay.Requested.Count);
        Assert.All(delay.Requested, d => Assert.Equal(TimeSpan.FromSeconds(3), d));
    }

    private static void EnvironmentRateLimitSimulatorReset(int remaining, int retryAfterSeconds)
    {
        var m = typeof(EnvironmentRateLimitSimulator).GetMethod(
            "ResetForTests",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
        m!.Invoke(null, new object[] { remaining, retryAfterSeconds });
    }
}
