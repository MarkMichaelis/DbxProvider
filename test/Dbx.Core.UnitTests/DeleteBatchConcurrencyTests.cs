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
/// Verifies <see cref="DropboxServiceClient.DeleteBatchesAsync"/> overlaps batch
/// jobs up to the requested concurrency, never exceeds it, and aggregates
/// per-entry failures across every chunk.
/// </summary>
public class DeleteBatchConcurrencyTests
{
    /// <summary>
    /// Test double that records how many <see cref="DeleteBatchAsync"/> calls run
    /// at the same time (so tests can assert the concurrency limit) and can be told
    /// which paths should come back as failures.
    /// </summary>
    private sealed class ConcurrencyProbeClient : DropboxServiceClient
    {
        private readonly HashSet<string> _failPaths;
        private int _current;
        private int _max;

        public ConcurrencyProbeClient(IEnumerable<string>? failPaths = null)
            : base((Dropbox.Api.DropboxClient)null!)
        {
            _failPaths = new HashSet<string>(failPaths ?? Enumerable.Empty<string>(), StringComparer.Ordinal);
        }

        public int MaxObservedConcurrency => _max;

        public override async Task<IReadOnlyList<DropboxBatchDeleteError>> DeleteBatchAsync(
            IEnumerable<string> paths, Action<int>? onItemsProcessed = null,
            CancellationToken cancellationToken = default)
        {
            int now = Interlocked.Increment(ref _current);
            int observed;
            do
            {
                observed = Volatile.Read(ref _max);
                if (now <= observed) break;
            }
            while (Interlocked.CompareExchange(ref _max, now, observed) != observed);

            try
            {
                await Task.Delay(100, cancellationToken).ConfigureAwait(false);
                return paths
                    .Where(p => _failPaths.Contains(p))
                    .Select(p => new DropboxBatchDeleteError(p, "path not found"))
                    .ToList();
            }
            finally
            {
                Interlocked.Decrement(ref _current);
            }
        }
    }

    private static List<IReadOnlyList<string>> MakeChunks(int count) =>
        Enumerable.Range(0, count)
            .Select(i => (IReadOnlyList<string>)new[] { $"/chunk{i}/a", $"/chunk{i}/b" })
            .ToList();

    [Fact]
    public async Task DeleteBatchesAsync_OverlapsJobs_WithoutExceedingMaxConcurrency()
    {
        var client = new ConcurrencyProbeClient();
        var chunks = MakeChunks(6);

        await client.DeleteBatchesAsync(chunks, maxConcurrency: 2);

        client.MaxObservedConcurrency.Should().Be(2,
            "with 6 chunks and a limit of 2, exactly two jobs should run at once");
    }

    [Fact]
    public async Task DeleteBatchesAsync_RaisesConcurrency_WhenLimitIsHigher()
    {
        var client = new ConcurrencyProbeClient();
        var chunks = MakeChunks(8);

        await client.DeleteBatchesAsync(chunks, maxConcurrency: 4);

        client.MaxObservedConcurrency.Should().Be(4,
            "a higher limit should let more jobs overlap than the serial path");
    }

    [Fact]
    public async Task DeleteBatchesAsync_AggregatesFailures_AcrossAllChunks()
    {
        var failing = new[] { "/chunk1/a", "/chunk3/b" };
        var client = new ConcurrencyProbeClient(failing);
        var chunks = MakeChunks(5);

        var failures = await client.DeleteBatchesAsync(chunks, maxConcurrency: 3);

        failures.Select(f => f.Path).Should().BeEquivalentTo(failing);
        failures.Should().OnlyContain(f => f.Reason == "path not found");
    }

    [Fact]
    public async Task DeleteBatchesAsync_InvokesCallback_WithEveryChunkCount()
    {
        var client = new ConcurrencyProbeClient();
        var chunks = MakeChunks(5);
        int reported = 0;

        await client.DeleteBatchesAsync(chunks, maxConcurrency: 2,
            onChunkCompleted: n => Interlocked.Add(ref reported, n));

        reported.Should().Be(chunks.Sum(c => c.Count),
            "the callback should account for every deleted entry exactly once");
    }

    /// <summary>Test double whose delete throws for chunks containing a marked path.</summary>
    private sealed class ThrowingProbeClient : DropboxServiceClient
    {
        private readonly string _throwForChunkPrefix;

        public ThrowingProbeClient(string throwForChunkPrefix)
            : base((Dropbox.Api.DropboxClient)null!) => _throwForChunkPrefix = throwForChunkPrefix;

        public override Task<IReadOnlyList<DropboxBatchDeleteError>> DeleteBatchAsync(
            IEnumerable<string> paths, Action<int>? onItemsProcessed = null,
            CancellationToken cancellationToken = default)
        {
            var list = paths.ToList();
            if (list.Any(p => p.StartsWith(_throwForChunkPrefix, StringComparison.Ordinal)))
            {
                throw new InvalidOperationException("batch delete job failed");
            }

            return Task.FromResult((IReadOnlyList<DropboxBatchDeleteError>)Array.Empty<DropboxBatchDeleteError>());
        }
    }

    [Fact]
    public async Task DeleteBatchesAsync_ConvertsChunkException_ToPerPathFailures_WithoutAbortingOtherChunks()
    {
        // When one chunk's delete throws, its paths must surface as per-path failures
        // (with the real paths) while the other chunks still complete -- never abort
        // the whole window and lose those paths. Reverting to a bare try/finally lets
        // the exception escape Task.WhenAll, so DeleteBatchesAsync throws and this fails.
        var client = new ThrowingProbeClient(throwForChunkPrefix: "/chunk1/");
        var chunks = MakeChunks(3);

        var failures = await client.DeleteBatchesAsync(chunks, maxConcurrency: 3);

        failures.Select(f => f.Path).Should().BeEquivalentTo(new[] { "/chunk1/a", "/chunk1/b" },
            "the failing chunk's real paths are preserved as failures");
        failures.Should().OnlyContain(f => f.Reason == "batch delete job failed");
    }
}
