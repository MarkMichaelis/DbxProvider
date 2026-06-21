using System.Linq;
using DbxProvider.Cmdlets;
using Xunit;

namespace DbxProvider.ProviderHostTests;

/// <summary>
/// Behavior-first tests for <see cref="TransientRetryThrottle"/>: the relentless,
/// auto-retried <c>too_many_write_operations</c> notices emitted during a large
/// concurrent batch delete must be surfaced sparingly (a first heartbeat, then one
/// every <see cref="TransientRetryThrottle.WarnEvery"/>) so the live progress bar
/// is not buried under a wall of identical warnings. Reverting to "warn every time"
/// makes every call return <c>true</c>, failing these tests behaviorally.
/// </summary>
public class TransientRetryThrottleTests
{
    [Fact]
    public void ShouldWarn_WarnsOnFirstThenEveryWarnEvery_NotEveryTime()
    {
        var throttle = new TransientRetryThrottle();

        // Drive 50 transient retries and capture which ones surface as warnings.
        var warned = Enumerable.Range(1, 50).Where(_ => throttle.ShouldWarn()).Count();

        // First occurrence (heartbeat) plus one at the WarnEvery boundary (26th) --
        // two warnings for fifty retries, not fifty.
        Assert.Equal(2, warned);
    }

    [Fact]
    public void ShouldWarn_FirstCallIsAlwaysAHeartbeatWarning()
    {
        var throttle = new TransientRetryThrottle();

        // The user must learn immediately that the run is being throttled.
        Assert.True(throttle.ShouldWarn());
        // The immediately following retries are demoted (no flood).
        Assert.False(throttle.ShouldWarn());
        Assert.False(throttle.ShouldWarn());
    }

    [Fact]
    public void SuppressedSinceLastWarn_CountsTheDemotedRetries()
    {
        var throttle = new TransientRetryThrottle();

        // Walk exactly up to the second warning (the WarnEvery-th call after the
        // first) and confirm it reports how many retries it stands in for.
        for (int i = 1; i < 1 + TransientRetryThrottle.WarnEvery; i++)
        {
            bool warned = throttle.ShouldWarn();
            if (i == 1) Assert.True(warned);          // first heartbeat
            else if (i < 1 + TransientRetryThrottle.WarnEvery) Assert.False(warned);
        }
        // The call at position (1 + WarnEvery) is the next warning.
        Assert.True(throttle.ShouldWarn());
        Assert.Equal(TransientRetryThrottle.WarnEvery - 1, throttle.SuppressedSinceLastWarn);
    }
}
