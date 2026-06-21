using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using IntelliTect.Dropbox;
using Xunit;

namespace Dbx.Core.UnitTests;

/// <summary>
/// Verifies that a delete batch automatically re-submits the entries that fail
/// with the transient <c>too_many_write_operations</c> lock error (raised when
/// overlapping deletes contend on the namespace) instead of abandoning them, and
/// that genuinely permanent failures pass straight through without retrying.
/// </summary>
public class DeleteBatchTransientRetryTests
{
    private sealed class RetryProbeClient : DropboxServiceClient
    {
        public RetryProbeClient() : base((Dropbox.Api.DropboxClient)null!) { }
    }

    /// <summary>Test delay that records every requested wait without sleeping.</summary>
    private sealed class RecordingDelay : IDelay
    {
        public List<TimeSpan> Waits { get; } = new();
        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            Waits.Add(delay);
            return Task.CompletedTask;
        }
    }

    private const int MaxAttempts = 6;

    [Fact]
    public async Task RetryTransientDeletes_ResubmitsOnlyContendedPaths_UntilTheySucceed()
    {
        // /c deletes on the first try; /a and /b hit the transient lock error and
        // must be re-submitted (only those two, not /c) and then succeed -- so the
        // batch reports zero failures despite the initial contention.
        var client = new RetryProbeClient();
        var delay = new RecordingDelay();
        var attempts = new List<IReadOnlyList<string>>();
        int call = 0;

        Task<DropboxServiceClient.DeleteAttemptResult> Submit(
            IReadOnlyList<string> paths, CancellationToken ct)
        {
            attempts.Add(paths.ToList());
            call++;
            return Task.FromResult(call == 1
                ? new DropboxServiceClient.DeleteAttemptResult(
                    Array.Empty<DropboxBatchDeleteError>(), new[] { "/a", "/b" })
                : new DropboxServiceClient.DeleteAttemptResult(
                    Array.Empty<DropboxBatchDeleteError>(), Array.Empty<string>()));
        }

        var failures = await client.RetryTransientDeletesAsync(
            new[] { "/a", "/b", "/c" }, Submit, delay, null, CancellationToken.None);

        failures.Should().BeEmpty("the contended paths succeed on re-submission");
        attempts.Should().HaveCount(2);
        attempts[1].Should().BeEquivalentTo(new[] { "/a", "/b" },
            "only the contended paths are retried, not the ones that already deleted");
        delay.Waits.Should().HaveCount(1, "exactly one backoff between the two attempts");
    }

    [Fact]
    public async Task RetryTransientDeletes_DoesNotRetryPermanentFailures()
    {
        // A non-transient failure (e.g. an unexpected error) must be reported
        // immediately without consuming any retry attempts or backoff.
        var client = new RetryProbeClient();
        var delay = new RecordingDelay();
        int call = 0;

        Task<DropboxServiceClient.DeleteAttemptResult> Submit(
            IReadOnlyList<string> paths, CancellationToken ct)
        {
            call++;
            return Task.FromResult(new DropboxServiceClient.DeleteAttemptResult(
                new[] { new DropboxBatchDeleteError("/x", "path not found") },
                Array.Empty<string>()));
        }

        var failures = await client.RetryTransientDeletesAsync(
            new[] { "/x" }, Submit, delay, null, CancellationToken.None);

        failures.Select(f => f.Path).Should().Equal("/x");
        failures.Single().Reason.Should().Be("path not found");
        call.Should().Be(1, "permanent failures are not retried");
        delay.Waits.Should().BeEmpty();
    }

    [Fact]
    public async Task RetryTransientDeletes_GivesUpAfterBudget_ReportingContendedPaths()
    {
        // If a path stays contended past the retry budget it must surface as a
        // failure so the caller re-queues it next run -- never silently dropped,
        // never an infinite loop.
        var client = new RetryProbeClient();
        var delay = new RecordingDelay();
        int call = 0;

        Task<DropboxServiceClient.DeleteAttemptResult> Submit(
            IReadOnlyList<string> paths, CancellationToken ct)
        {
            call++;
            return Task.FromResult(new DropboxServiceClient.DeleteAttemptResult(
                Array.Empty<DropboxBatchDeleteError>(), new[] { "/a" }));
        }

        var failures = await client.RetryTransientDeletesAsync(
            new[] { "/a" }, Submit, delay, null, CancellationToken.None);

        call.Should().Be(MaxAttempts, "the retry budget bounds the attempts");
        delay.Waits.Should().HaveCount(MaxAttempts - 1);
        failures.Select(f => f.Path).Should().Equal("/a");
        failures.Single().Reason.Should().Be("too_many_write_operations");
    }

    [Fact]
    public async Task RetryTransientDeletes_GivesUp_LabelsFailuresWithTransientReason_WhenProvided()
    {
        // A whole-job failure is retried via the transient path carrying its own
        // reason. If it exhausts the budget the surfaced failures must report THAT
        // reason ("batch delete job failed"), not the default lock label -- otherwise
        // the operator cannot tell a genuine job failure from write contention.
        // Reverting to the hard-coded "too_many_write_operations" label fails this.
        var client = new RetryProbeClient();
        var delay = new RecordingDelay();

        Task<DropboxServiceClient.DeleteAttemptResult> Submit(
            IReadOnlyList<string> paths, CancellationToken ct) =>
            Task.FromResult(new DropboxServiceClient.DeleteAttemptResult(
                Array.Empty<DropboxBatchDeleteError>(), new[] { "/a" }, "batch delete job failed"));

        var failures = await client.RetryTransientDeletesAsync(
            new[] { "/a" }, Submit, delay, null, CancellationToken.None);

        failures.Single().Reason.Should().Be("batch delete job failed",
            "the exhausted transient reason reflects the actual transient cause");
    }

    [Fact]
    public void DescribeDeleteBatchError_MapsContention_ToTooManyWriteOperations()
    {
        // A whole-job Failed state caused by namespace write contention must be
        // labeled "too_many_write_operations" so the operator (and failed.csv) can
        // tell recoverable contention from a genuine fault. Reverting to a single
        // generic label collapses the two and fails this assertion.
        var reason = DropboxServiceClient.DescribeDeleteBatchError(
            Dropbox.Api.Files.DeleteBatchError.TooManyWriteOperations.Instance);

        reason.Should().Be("too_many_write_operations");
    }

    [Fact]
    public void DescribeDeleteBatchError_MapsOther_ToGenericJobFailure()
    {
        // Any non-contention job failure falls back to the generic label rather than
        // masquerading as contention -- so it is not silently treated as routine.
        var reason = DropboxServiceClient.DescribeDeleteBatchError(
            Dropbox.Api.Files.DeleteBatchError.Other.Instance);

        reason.Should().Be("batch delete job failed");
    }

    [Fact]
    public async Task RetryTransientDeletes_ReportsSuccessesIncrementally_AsContendedPathsClear()
    {
        // The first attempt deletes /c immediately while /a and /b stay contended;
        // the second attempt clears /a and /b. Progress must be reported as each
        // subset succeeds (1, then 2) -- not lumped into a single final total -- so a
        // progress bar climbs steadily through the multi-attempt wait instead of
        // jumping only when the whole batch finishes. Reverting to a single
        // end-of-batch report changes the observed sequence, so this fails
        // behaviorally.
        var client = new RetryProbeClient();
        var delay = new RecordingDelay();
        int call = 0;

        Task<DropboxServiceClient.DeleteAttemptResult> Submit(
            IReadOnlyList<string> paths, CancellationToken ct)
        {
            call++;
            return Task.FromResult(call == 1
                ? new DropboxServiceClient.DeleteAttemptResult(
                    Array.Empty<DropboxBatchDeleteError>(), new[] { "/a", "/b" })
                : new DropboxServiceClient.DeleteAttemptResult(
                    Array.Empty<DropboxBatchDeleteError>(), Array.Empty<string>()));
        }

        var reported = new List<int>();
        var failures = await client.RetryTransientDeletesAsync(
            new[] { "/a", "/b", "/c" }, Submit, delay, n => reported.Add(n), CancellationToken.None);

        failures.Should().BeEmpty();
        reported.Should().Equal(new[] { 1, 2 },
            "the immediate success is reported as it happens, then the two contended " +
            "paths once they clear -- progress advances per attempt, not all at once");
    }

    [Fact]
    public async Task RetryTransientDeletes_ReportsAlreadyGonePaths_AsProcessed()
    {
        // A batch whose paths were all deleted by a prior run comes back as permanent
        // "not found" failures with nothing transient. Those paths still reached a
        // terminal state, so they must be reported as processed -- otherwise the
        // progress bar stalls at zero while churning through manifest regions that are
        // already clear (the common case once most conflicts are gone). Reverting to
        // counting only successful deletes makes the report empty, failing this test.
        var client = new RetryProbeClient();
        var delay = new RecordingDelay();

        Task<DropboxServiceClient.DeleteAttemptResult> Submit(
            IReadOnlyList<string> paths, CancellationToken ct) =>
            Task.FromResult(new DropboxServiceClient.DeleteAttemptResult(
                paths.Select(p => new DropboxBatchDeleteError(p, "path not found")).ToList(),
                Array.Empty<string>()));

        var reported = new List<int>();
        var failures = await client.RetryTransientDeletesAsync(
            new[] { "/gone1", "/gone2", "/gone3" }, Submit, delay, n => reported.Add(n),
            CancellationToken.None);

        failures.Should().HaveCount(3, "every already-gone path surfaces as a permanent failure");
        reported.Should().Equal(new[] { 3 },
            "all three already-gone paths are reported as processed in one terminal step");
    }
}
