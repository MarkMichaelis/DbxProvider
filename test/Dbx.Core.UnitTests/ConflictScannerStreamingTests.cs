using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using IntelliTect.Dropbox;
using Xunit;

namespace Dbx.Core.UnitTests;

/// <summary>
/// Behavior-first tests proving that a cold conflict scan streams the recursive
/// listing page-by-page (via list_folder first-page + continue) rather than
/// buffering the whole tree, so it finds nested matches across every page and
/// reports progress between pages. Reverting the scanner to a single buffered
/// <c>ListFolderWithCursorAsync</c> call makes these fail behaviorally: that
/// fake path returns only direct children, so the deeply nested matches vanish.
/// </summary>
public class ConflictScannerStreamingTests
{
    private const string Pattern = "*'s conflicted copy*";

    private static DropboxItem Conflict(string path) =>
        new() { Name = path.Split('/').Last(), Path = path, IsFolder = false, Length = 0 };

    private static DropboxItem File(string path, ulong bytes = 10) =>
        new() { Name = path.Split('/').Last(), Path = path, IsFolder = false, Length = bytes };

    [Fact]
    public async Task ColdScan_PagesThroughEntireRecursiveTree_FindsNestedMatches()
    {
        var items = new List<DropboxItem>
        {
            File("/A/keep.txt"),
            Conflict("/A/a's conflicted copy.txt"),
            File("/A/B/keep.txt"),
            Conflict("/A/B/b's conflicted copy.txt"),
            File("/A/B/C/keep.txt"),
            Conflict("/A/B/C/c's conflicted copy.txt"),
        };
        var fake = new FakeListServiceClient(items) { PageSize = 2 };
        var scanner = new ConflictScanner(fake);

        var progressMatchCounts = new List<int>();
        var result = await scanner.ScanAsync(
            new ConflictScanParameters { Pattern = Pattern },
            previousState: null,
            onProgress: state => progressMatchCounts.Add(state.Matches.Count));

        Assert.True(result.WasFullScan);
        Assert.Equal(1, fake.FirstPageCalls);
        Assert.True(fake.ContinueCalls > 0, "cold scan should page via list_folder/continue, not buffer everything");

        var matched = result.Matches.Select(m => m.Path).OrderBy(p => p, System.StringComparer.Ordinal).ToList();
        Assert.Equal(
            new[]
            {
                "/A/B/C/c's conflicted copy.txt",
                "/A/B/b's conflicted copy.txt",
                "/A/a's conflicted copy.txt",
            }.OrderBy(p => p, System.StringComparer.Ordinal),
            matched);

        Assert.True(progressMatchCounts.Count > 1, "progress should be reported once per page");
    }

    [Fact]
    public async Task ColdScan_NonZeroConflicts_ExcludedByDefault_AcrossPages()
    {
        var items = new List<DropboxItem>
        {
            Conflict("/A/zero's conflicted copy.txt"),
            File("/A/big's conflicted copy.bin", bytes: 500),
            Conflict("/A/B/zero2's conflicted copy.txt"),
        };
        var fake = new FakeListServiceClient(items) { PageSize = 1 };
        var scanner = new ConflictScanner(fake);

        var result = await scanner.ScanAsync(
            new ConflictScanParameters { Pattern = Pattern }, previousState: null);

        var matched = result.Matches.Select(m => m.Path).OrderBy(p => p, System.StringComparer.Ordinal).ToList();
        Assert.Equal(
            new[] { "/A/B/zero2's conflicted copy.txt", "/A/zero's conflicted copy.txt" },
            matched);
    }
}
