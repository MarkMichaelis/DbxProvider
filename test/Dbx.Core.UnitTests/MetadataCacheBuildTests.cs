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

        // One first-page recursive listing, no per-folder listings.
        service.FirstPageCalls.Should().Be(1);
        service.RecursiveListCalls.Should().Be(0);
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
        service.FirstPageCalls.Should().Be(0);
        cache.Count.Should().Be(0);
    }

    [Fact]
    public async Task BuildAsync_RequestsEnrichedMetadata()
    {
        var service = new FakeListServiceClient(SampleTree());
        using var cache = new MetadataCache(service, "acct", "user@example.com", Opts());

        await cache.BuildAsync("/");

        service.LastIncludeMediaInfo.Should().BeTrue();
        service.LastIncludeHasExplicitSharedMembers.Should().BeTrue();
    }

    [Fact]
    public async Task BuildAsync_InterruptedBuild_ResumesFromSavedCursor()
    {
        // First instance walks page 1, persists progress, then is interrupted
        // on the first continue call.
        var first = new FakeListServiceClient(SampleTree()) { PageSize = 2, ThrowOnContinueCall = 1 };
        using (var cache1 = new MetadataCache(first, "acct", "user@example.com", Opts()))
        {
            var act = async () => await cache1.BuildAsync("/");
            await act.Should().ThrowAsync<OperationCanceledException>();
            cache1.IsBuildComplete("/").Should().BeFalse();
        }

        // Second instance over the same database resumes from the saved cursor
        // instead of restarting from the first page.
        var second = new FakeListServiceClient(SampleTree()) { PageSize = 2 };
        using var cache2 = new MetadataCache(second, "acct", "user@example.com", Opts());

        await cache2.BuildAsync("/");

        second.FirstPageCalls.Should().Be(0, "the build resumes from the saved cursor");
        second.ContinueCalls.Should().BeGreaterThan(0);
        cache2.IsBuildComplete("/").Should().BeTrue();

        cache2.TryGet("/A", out var a).Should().BeTrue();
        a!.Items.Select(i => i.Path).Should().BeEquivalentTo(new[] { "/A/file2.txt", "/A/B" });
        cache2.TryGet("/A/B", out var b).Should().BeTrue();
        b!.Items.Select(i => i.Path).Should().BeEquivalentTo(new[] { "/A/B/file.txt" });
    }

    [Fact]
    public async Task BuildRevisionsAsync_CachesRevisionsForEveryFileSkippingFolders()
    {
        var service = new FakeListServiceClient(SampleTree());
        service.RevisionsByPath["/A/file2.txt"] = new List<DropboxRevision>
        {
            Revision("r1", 10),
            Revision("r2", 12),
        };
        service.RevisionsByPath["/A/B/file.txt"] = new List<DropboxRevision> { Revision("r3", 7) };
        using var cache = new MetadataCache(service, "acct", "user@example.com", Opts());
        await cache.BuildAsync("/");

        var result = await cache.BuildRevisionsAsync("/");

        result.FilesWithRevisionsCached.Should().Be(2);
        result.RevisionsCached.Should().Be(3);
        cache.RevisionCount().Should().Be(3);
        service.RevisionPathsRequested.Should()
            .BeEquivalentTo(new[] { "/A/file2.txt", "/A/B/file.txt" });
    }

    [Fact]
    public async Task BuildRevisionsAsync_ReportsProgressForEachFile()
    {
        var service = new FakeListServiceClient(SampleTree());
        service.RevisionsByPath["/A/file2.txt"] = new List<DropboxRevision> { Revision("r1", 10) };
        service.RevisionsByPath["/A/B/file.txt"] = new List<DropboxRevision> { Revision("r2", 7) };
        using var cache = new MetadataCache(service, "acct", "user@example.com", Opts());
        await cache.BuildAsync("/");

        var totals = new List<int>();
        await cache.BuildRevisionsAsync("/", onProgress: (processed, total) => totals.Add(total));

        totals.Should().NotBeEmpty();
        totals.Should().OnlyContain(t => t == 2);
    }

    [Fact]
    public async Task BuildRevisionsAsync_FreshRevisions_AreSkippedOnSecondPass()
    {
        var service = new FakeListServiceClient(SampleTree());
        service.RevisionsByPath["/A/file2.txt"] = new List<DropboxRevision> { Revision("r1", 10) };
        service.RevisionsByPath["/A/B/file.txt"] = new List<DropboxRevision> { Revision("r2", 7) };
        using var cache = new MetadataCache(service, "acct", "user@example.com", Opts());
        await cache.BuildAsync("/");
        await cache.BuildRevisionsAsync("/");
        service.RevisionPathsRequested.Clear();

        var second = await cache.BuildRevisionsAsync("/", maxAge: TimeSpan.FromHours(1));

        second.FilesWithRevisionsCached.Should().Be(0);
        service.RevisionPathsRequested.Should().BeEmpty();
    }

    private static DropboxRevision Revision(string rev, ulong length) => new()
    {
        Rev = rev,
        Length = length,
        ContentHash = "hash-" + rev,
        ServerModified = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        ClientModified = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
    };
}