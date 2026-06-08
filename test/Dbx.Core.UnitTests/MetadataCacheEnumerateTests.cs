using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using IntelliTect.Dropbox;
using Xunit;

namespace Dbx.Core.UnitTests;

/// <summary>
/// Behavior tests for <see cref="MetadataCache.EnumerateItems"/> and
/// <see cref="MetadataCache.FindItems"/> -- reading cached items straight from
/// the local database with zero Dropbox API calls.
/// </summary>
public sealed class MetadataCacheEnumerateTests : IDisposable
{
    private readonly string _tempRoot =
        Path.Combine(Path.GetTempPath(), "DbxEnumerateCacheTests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot))
        {
            try { Directory.Delete(_tempRoot, recursive: true); } catch { /* best effort */ }
        }
    }

    private CacheOptions Opts(bool enabled = true) => new()
    {
        Enabled = enabled,
        RootDirectoryOverride = _tempRoot,
        FlushIntervalSeconds = 0
    };

    private static DropboxItem Folder(string path) =>
        new() { Path = path, Name = path.Split('/').Last(), IsFolder = true, Id = "id:" + path };

    private static DropboxItem File(string path, ulong length = 0) =>
        new() { Path = path, Name = path.Split('/').Last(), IsFolder = false, Length = length, Id = "id:" + path };

    private static List<DropboxItem> SampleTree() => new()
    {
        Folder("/A"),
        File("/A/file2.txt"),
        Folder("/A/B"),
        File("/A/B/file.txt"),
    };

    [Fact]
    public async Task EnumerateItems_AfterBuild_ReturnsEveryItem_WithZeroExtraApiCalls()
    {
        var service = new FakeListServiceClient(SampleTree());
        using var cache = new MetadataCache(service, "acct", "user@example.com", Opts());
        await cache.BuildAsync("/");

        // Snapshot the API counters AFTER the build so we can prove enumeration
        // adds nothing.
        var firstPage = service.FirstPageCalls;
        var continueCalls = service.ContinueCalls;
        var latestCursor = service.GetLatestCursorCalls;

        var items = cache.EnumerateItems().ToList();

        items.Select(i => i.Path).Should().BeEquivalentTo(new[]
        {
            "/A", "/A/file2.txt", "/A/B", "/A/B/file.txt"
        });

        // Pure cache read: not a single additional listing/continue/cursor call.
        service.FirstPageCalls.Should().Be(firstPage);
        service.RecursiveListCalls.Should().Be(0);
        service.NonRecursiveListCalls.Should().Be(0);
        service.ContinueCalls.Should().Be(continueCalls);
        service.GetLatestCursorCalls.Should().Be(latestCursor);
    }

    [Fact]
    public async Task EnumerateItems_Subtree_ReturnsOnlyDescendantsOfStartPath()
    {
        var service = new FakeListServiceClient(SampleTree());
        using var cache = new MetadataCache(service, "acct", "user@example.com", Opts());
        await cache.BuildAsync("/");

        cache.EnumerateItems("/A/B").Select(i => i.Path)
            .Should().BeEquivalentTo(new[] { "/A/B/file.txt" });

        cache.EnumerateItems("/A").Select(i => i.Path)
            .Should().BeEquivalentTo(new[] { "/A/file2.txt", "/A/B", "/A/B/file.txt" });
    }

    [Fact]
    public async Task FindItems_ZeroByteConflictPredicate_ReturnsOnlyMatchingFiles()
    {
        var tree = new List<DropboxItem>
        {
            Folder("/A"),
            File("/A/report's conflicted copy.txt", length: 0),
            File("/A/data's conflicted copy.txt", length: 100),
            File("/A/normal.txt", length: 50),
        };
        var service = new FakeListServiceClient(tree);
        using var cache = new MetadataCache(service, "acct", "user@example.com", Opts());
        await cache.BuildAsync("/");

        var matches = cache.FindItems(
            i => !i.IsFolder && i.Length == 0 && i.Name.Contains("conflicted copy"));

        matches.Select(i => i.Path)
            .Should().BeEquivalentTo(new[] { "/A/report's conflicted copy.txt" });
    }
}