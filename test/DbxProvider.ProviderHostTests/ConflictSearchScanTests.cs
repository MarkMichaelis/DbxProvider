using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using IntelliTect.Dropbox;
using Xunit;

namespace DbxProvider.ProviderHostTests;

/// <summary>
/// Behavior-first tests for <see cref="ConflictScanner.SearchScanAsync"/>, the
/// fast search_v2-backed cold-discovery path. They drive the scanner against
/// <see cref="FakeDropboxServiceClient"/>, which scripts scope-keyed search
/// pages, links them with synthetic cursors, and counts search calls.
/// </summary>
public class ConflictSearchScanTests
{
    private const string ConflictPattern = "*'s conflicted copy*";

    private static DropboxItem Conflict(string path, ulong bytes = 0) =>
        new() { Name = path.Split('/').Last(), Path = path, IsFolder = false, Length = bytes };

    private static DropboxItem File(string path, ulong bytes = 10) =>
        new() { Name = path.Split('/').Last(), Path = path, IsFolder = false, Length = bytes };

    private static DropboxItem Folder(string path) =>
        new() { Name = path.Split('/').Last(), Path = path, IsFolder = true };

    private static ConflictScanParameters Params(string startPath = "", string pattern = ConflictPattern, bool includeNonZero = false) =>
        new() { StartPath = startPath, Pattern = pattern, IncludeNonZero = includeNonZero };

    [Fact]
    public async Task SearchScan_ReturnsConflictMatches_AndPostFiltersNonMatchingNames()
    {
        var fake = new FakeDropboxServiceClient(new List<DropboxItem>());
        // search_v2 token query "conflicted copy" can return near-misses; the
        // wildcard post-filter must drop names that don't match the pattern.
        fake.EnqueueSearchPage("", new[]
        {
            Conflict("/A/report's conflicted copy 2024.docx"),
            File("/A/conflicted draft.txt", bytes: 0), // no "'s conflicted copy" -> filtered out
        });
        var scanner = new ConflictScanner(fake);

        var result = await scanner.SearchScanAsync(Params());

        var match = Assert.Single(result.Matches);
        Assert.Equal("/A/report's conflicted copy 2024.docx", match.Path);
        Assert.True(fake.SearchCalls >= 1);
    }

    [Fact]
    public async Task SearchScan_ZeroByteRule_ExcludesNonZero_UnlessIncludeNonZeroSet()
    {
        var fake = new FakeDropboxServiceClient(new List<DropboxItem>());
        fake.EnqueueSearchPage("", new[]
        {
            Conflict("/A/zero's conflicted copy.txt", bytes: 0),
            Conflict("/A/big's conflicted copy.bin", bytes: 500),
        });
        var scanner = new ConflictScanner(fake);

        var zeroOnly = await scanner.SearchScanAsync(Params());
        var match = Assert.Single(zeroOnly.Matches);
        Assert.Equal("/A/zero's conflicted copy.txt", match.Path);

        // Re-enqueue (the first scan drained the page) and include non-zero.
        fake.EnqueueSearchPage("", new[]
        {
            Conflict("/A/zero's conflicted copy.txt", bytes: 0),
            Conflict("/A/big's conflicted copy.bin", bytes: 500),
        });
        var all = await scanner.SearchScanAsync(Params(includeNonZero: true));
        Assert.Equal(2, all.Matches.Count);
        Assert.Contains(all.Matches, m => m.Path == "/A/big's conflicted copy.bin" && m.Bytes == 500);
    }

    [Fact]
    public async Task SearchScan_FollowsCursor_AcrossMultiplePages()
    {
        var fake = new FakeDropboxServiceClient(new List<DropboxItem>());
        fake.EnqueueSearchPage("", new[] { Conflict("/A/one's conflicted copy.txt") });
        fake.EnqueueSearchPage("", new[] { Conflict("/B/two's conflicted copy.txt") });
        var scanner = new ConflictScanner(fake);

        var result = await scanner.SearchScanAsync(Params());

        Assert.Equal(2, result.Matches.Count);
        Assert.Equal(2, fake.SearchCalls); // both pages fetched by following the cursor
        Assert.Contains(result.Matches, m => m.Path == "/A/one's conflicted copy.txt");
        Assert.Contains(result.Matches, m => m.Path == "/B/two's conflicted copy.txt");
    }

    [Fact]
    public async Task SearchScan_AtCeiling_SubdividesIntoChildFolders_UnionsAndDedupes()
    {
        var fake = new FakeDropboxServiceClient(new List<DropboxItem> { Folder("/A"), Folder("/B") });
        // Ceiling of 2: the root scope fills up and may be incomplete.
        fake.EnqueueSearchPage("", new[]
        {
            Conflict("/A/x's conflicted copy.txt"),
            Conflict("/B/y's conflicted copy.txt"),
        });
        // Subdivision searches each child folder. /A also surfaces a third match
        // the capped root scope missed; the /A/x hit is a duplicate to dedupe.
        fake.EnqueueSearchPage("/A", new[]
        {
            Conflict("/A/x's conflicted copy.txt"),       // duplicate of root hit
            Conflict("/A/z's conflicted copy.txt"),       // only found via subdivision
        });
        fake.EnqueueSearchPage("/B", new[] { Conflict("/B/y's conflicted copy.txt") });
        var scanner = new ConflictScanner(fake, searchResultCeiling: 2);

        var result = await scanner.SearchScanAsync(Params());

        var paths = result.Matches.Select(m => m.Path).OrderBy(p => p).ToList();
        Assert.Equal(new[]
        {
            "/A/x's conflicted copy.txt",
            "/A/z's conflicted copy.txt",
            "/B/y's conflicted copy.txt",
        }, paths);
    }
}