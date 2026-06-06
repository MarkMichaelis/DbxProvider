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
/// Behavior tests for <see cref="MetadataCache.BuildAsync"/> -- pre-populating
/// the cache from a single recursive listing.
/// </summary>
public sealed class MetadataCacheBuildTests : IDisposable
{
    private readonly string _tempRoot =
        Path.Combine(Path.GetTempPath(), "DbxBuildCacheTests-" + Guid.NewGuid().ToString("N"));

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

    private static DropboxItem File(string path) =>
        new() { Path = path, Name = path.Split('/').Last(), IsFolder = false, Id = "id:" + path };

    private static List<DropboxItem> SampleTree() => new()
    {
        Folder("/A"),
        File("/A/file2.txt"),
        Folder("/A/B"),
        File("/A/B/file.txt"),
    };

    [Fact]
    public async Task BuildAsync_RecursiveTree_CreatesPerFolderEntries()
    {
        var service = new FakeListServiceClient(SampleTree());
        using var cache = new MetadataCache(service, "acct", "user@example.com", Opts());

        var result = await cache.BuildAsync("/");

        // One recursive listing, no per-folder listings.
        service.RecursiveListCalls.Should().Be(1);
        service.NonRecursiveListCalls.Should().Be(0);

        result.ItemsFound.Should().Be(4);
        result.FoldersCached.Should().Be(3); // root, /A, /A/B

        cache.TryGet("/", out var root).Should().BeTrue();
        root!.Items.Select(i => i.Path).Should().BeEquivalentTo(new[] { "/A" });

        cache.TryGet("/A", out var a).Should().BeTrue();
        a!.Items.Select(i => i.Path).Should().BeEquivalentTo(new[] { "/A/file2.txt", "/A/B" });

        cache.TryGet("/A/B", out var b).Should().BeTrue();
        b!.Items.Select(i => i.Path).Should().BeEquivalentTo(new[] { "/A/B/file.txt" });
    }

    [Fact]
    public async Task BuildAsync_BuiltEntry_AcquiresPerFolderCursorOnFirstRead()
    {
        var service = new FakeListServiceClient(SampleTree());
        using var cache = new MetadataCache(service, "acct", "user@example.com", Opts());

        await cache.BuildAsync("/");

        // Built entries start without a per-folder cursor.
        cache.TryGet("/A", out var beforeRead).Should().BeTrue();
        beforeRead!.Cursor.Should().BeEmpty();

        // First validated read must acquire a real per-folder cursor.
        var children = await cache.GetChildrenAsync("/A");
        children.Select(i => i.Path).Should().BeEquivalentTo(new[] { "/A/file2.txt", "/A/B" });

        cache.TryGet("/A", out var afterRead).Should().BeTrue();
        afterRead!.Cursor.Should().NotBeEmpty();
    }

    [Fact]
    public async Task BuildAsync_CacheDisabled_IsNoOp()
    {
        var service = new FakeListServiceClient(SampleTree());
        using var cache = new MetadataCache(service, "acct", "user@example.com", Opts(enabled: false));

        var result = await cache.BuildAsync("/");

        result.FoldersCached.Should().Be(0);
        result.ItemsFound.Should().Be(0);
        service.RecursiveListCalls.Should().Be(0);
        cache.Count.Should().Be(0);
    }
}