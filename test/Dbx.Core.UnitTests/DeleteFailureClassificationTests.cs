using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using IntelliTect.Dropbox;
using Xunit;

namespace Dbx.Core.UnitTests;

/// <summary>
/// Guards how delete failures are classified as transient (retry in-run with
/// backoff) vs permanent. A long delete run hit thousands of Dropbox
/// <c>internal_error</c> server faults and <c>HttpRequestException</c> network
/// blips that were being recorded as permanent failures; these must instead be
/// treated as transient so they are retried within the run.
/// </summary>
public class DeleteFailureClassificationTests
{
    [Theory]
    [InlineData("internal_error")]
    [InlineData("internal_error/")]
    [InlineData("too_many_write_operations")]
    [InlineData("An error occurred while sending the request.")]
    [InlineData("The operation has timed out.")]
    [InlineData("Service Unavailable")]
    public void IsTransientDeleteReason_TreatsRecoverableServerAndNetworkFaults_AsTransient(string reason)
    {
        // These are recoverable: re-issuing the (idempotent) delete later succeeds.
        // Reverting the classifier so only contention is transient fails this --
        // exactly the bug that recorded 1,000 internal_error/network blips as
        // permanent failures.
        DropboxServiceClient.IsTransientDeleteReason(reason).Should().BeTrue();
    }

    [Theory]
    [InlineData("path not found")]
    [InlineData("too_many_files")]
    [InlineData("")]
    [InlineData(null)]
    public void IsTransientDeleteReason_TreatsDeterministicFailures_AsPermanent(string? reason)
    {
        // A genuinely permanent failure must NOT be retried -- otherwise the run
        // burns the whole backoff budget on a path that can never succeed.
        DropboxServiceClient.IsTransientDeleteReason(reason).Should().BeFalse();
    }

    [Fact]
    public void IsTransientTransportException_TreatsNetworkFaults_AsTransient()
    {
        // A network blip on the submit/poll call applied nothing; the chunk must be
        // re-queued for backoff retry, not failed. Reverting the per-chunk handling
        // so these become permanent failures fails this.
        DropboxServiceClient.IsTransientTransportException(new HttpRequestException("boom"))
            .Should().BeTrue();
        DropboxServiceClient.IsTransientTransportException(new TimeoutException())
            .Should().BeTrue();
        DropboxServiceClient.IsTransientTransportException(new IOException("reset"))
            .Should().BeTrue();
    }

    [Fact]
    public void IsTransientTransportException_NeverTreatsCancellation_AsTransient()
    {
        // Ctrl+C must propagate as cancellation, never be swallowed as a transient
        // fault and silently retried.
        DropboxServiceClient.IsTransientTransportException(new OperationCanceledException())
            .Should().BeFalse();
        DropboxServiceClient.IsTransientTransportException(
            new TaskCanceledException()).Should().BeFalse();
    }

    [Theory]
    [InlineData("internal_error/", "internal_error")]
    [InlineData("  path_write/ ", "path_write")]
    [InlineData("", "unknown error")]
    [InlineData(null, "unknown error")]
    public void CleanReason_StripsTrailingSlashArtifact_FromSdkUnionToString(string? raw, string expected)
    {
        // The Dropbox union ToString() appends a trailing '/' for tags with no inner
        // value (e.g. "internal_error/"). The displayed/recorded reason must be the
        // clean tag. Reverting to the raw ToString() shows "internal_error/" and fails.
        DropboxServiceClient.CleanReason(raw).Should().Be(expected);
    }
}
