using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using IntelliTect.Dropbox;
using Xunit;

namespace DbxProvider.ProviderHostTests;

/// <summary>
/// Behavior-first tests for the shared, exhaustive and cap-safe
/// <see cref="DropboxServiceClient.SearchByFilenameAsync"/>. Every consumer
/// (provider auto-route, conflict scanner, NuGet callers) relies on this method
/// to page the search to completion and to subdivide into child folders when a
/// scope reaches the per-scope result ceiling, so no matches are silently
/// truncated.
/// </summary>
public class SearchByFilenameExhaustiveTests
{
    private static DropboxItem Match(string path, ulong bytes = 0) =>
        new() { Name = path.Split('/').Last(), Path = path, IsFolder = false, Length = bytes };

    private static DropboxItem Folder(string path) =>
        new() { Name = path.Split('/').Last(), Path = path, IsFolder = true };

    [Fact]
    public async Task SearchByFilename_IsExhaustive_AcrossPagesAndSubdivision()
    {
        var fake = new FakeDropboxServiceClient(new List<DropboxItem> { Folder("/A"), Folder("/B") });
        fake.SearchResultCeiling = 2; // root scope fills the ceiling and may be incomplete
        fake.EnqueueSearchPage("", new[]
        {
            Match("/A/one conflicted copy.txt"),
            Match("/B/two conflicted copy.txt"),
        });
        // Subdivision searches each child folder. /A surfaces a third match the
        // capped root scope missed; the /A/one hit is a duplicate to dedupe.
        fake.EnqueueSearchPage("/A", new[]
        {
            Match("/A/one conflicted copy.txt"),   // duplicate of root hit
            Match("/A/three conflicted copy.txt"), // only found via subdivision
        });
        fake.EnqueueSearchPage("/B", new[] { Match("/B/two conflicted copy.txt") });

        var result = await fake.SearchByFilenameAsync("*conflicted*");

        var paths = result.Select(i => i.Path).OrderBy(p => p).ToList();
        Assert.Equal(new[]
        {
            "/A/one conflicted copy.txt",
            "/A/three conflicted copy.txt",
            "/B/two conflicted copy.txt",
        }, paths);
    }

    [Fact]
    public async Task SearchByFilename_FollowsCursor_AcrossMultiplePages()
    {
        var fake = new FakeDropboxServiceClient(new List<DropboxItem>());
        fake.EnqueueSearchPage("", new[] { Match("/A/one conflicted copy.txt") });
        fake.EnqueueSearchPage("", new[] { Match("/B/two conflicted copy.txt") });

        var result = await fake.SearchByFilenameAsync("*conflicted*");

        Assert.Equal(2, result.Count);
        Assert.Equal(2, fake.SearchCalls); // both pages fetched by following the cursor
    }

    [Fact]
    public async Task SearchByFilename_HonorsExplicitMaxResults_StopsEarly()
    {
        var fake = new FakeDropboxServiceClient(new List<DropboxItem>());
        fake.EnqueueSearchPage("", new[]
        {
            Match("/A/a conflicted copy.txt"),
            Match("/A/b conflicted copy.txt"),
            Match("/A/c conflicted copy.txt"),
        });

        var result = await fake.SearchByFilenameAsync("*conflicted*", maxResults: 2);

        Assert.Equal(2, result.Count);
    }
}