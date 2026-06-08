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
/// Behavior tests for <see cref="MetadataCache"/> incremental refresh -- capturing
/// an account-wide delta cursor and draining <c>list_folder/continue</c> deltas into
/// the cached per-folder entries.
/// </summary>
public sealed class MetadataCacheSyncTests : IDisposable
{
    private readonly string _tempRoot =
        Path.Combine(Path.GetTempPath(), "DbxSyncCacheTests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot))
        {
            try { Directory.Delete(_tempRoot, recursive: true); } catch { /* best effort */ }
        }
    }

    private CacheOptions Opts() => new()
    {
        Enabled = true,
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
    public async Task EnsureSyncCursorAsync_NoCursor_CapturesAndPersists()
    {
        var service = new FakeListServiceClient(SampleTree());
        using var cache = new MetadataCache(service, "acct", "user@example.com", Opts());

        var captured = await cache.EnsureSyncCursorAsync();

        captured.Should().BeTrue();
        service.GetLatestCursorCalls.Should().Be(1);
        cache.GetSyncState().Should().NotBeNull();
        cache.GetSyncState()!.Cursor.Should().Be("sync::1");
    }

    [Fact]
    public async Task EnsureSyncCursorAsync_ExistingCursor_DoesNotOverwrite()
    {
        var service = new FakeListServiceClient(SampleTree());
        using var cache = new MetadataCache(service, "acct", "user@example.com", Opts());

        await cache.EnsureSyncCursorAsync();
        var captured = await cache.EnsureSyncCursorAsync();

        captured.Should().BeFalse();
        service.GetLatestCursorCalls.Should().Be(1);
        cache.GetSyncState()!.Cursor.Should().Be("sync::1");
    }

    [Fact]
    public async Task SyncAsync_NoCursorCaptured_Throws()
    {
        var service = new FakeListServiceClient(SampleTree());
        using var cache = new MetadataCache(service, "acct", "user@example.com", Opts());

        var act = () => cache.SyncAsync();

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task SyncAsync_DeltaAdd_LandsInParentEntry()
    {
        var service = new FakeListServiceClient(SampleTree());
        using var cache = new MetadataCache(service, "acct", "user@example.com", Opts());
        await cache.BuildAsync("/");
        await cache.EnsureSyncCursorAsync();
        service.SyncDeltas.Enqueue(MakeDelta("sync::done", hasMore: false, adds: new[] { File("/A/new.txt") }));

        var result = await cache.SyncAsync();

        result.Added.Should().Be(1);
        cache.TryGet("/A", out var entry).Should().BeTrue();
        entry!.Items.Select(i => i.Path).Should().Contain("/A/new.txt");
    }

    [Fact]
    public async Task SyncAsync_DeltaRemove_DropsItemFromParentEntry()
    {
        var service = new FakeListServiceClient(SampleTree());
        using var cache = new MetadataCache(service, "acct", "user@example.com", Opts());
        await cache.BuildAsync("/");
        await cache.EnsureSyncCursorAsync();
        service.SyncDeltas.Enqueue(MakeDelta("sync::done", hasMore: false, removes: new[] { "/a/file2.txt" }));

        var result = await cache.SyncAsync();

        result.Removed.Should().Be(1);
        cache.TryGet("/A", out var entry).Should().BeTrue();
        entry!.Items.Select(i => i.Path).Should().NotContain("/A/file2.txt");
    }

    [Fact]
    public async Task SyncAsync_MultiplePages_AdvancesAndPersistsCursor()
    {
        var service = new FakeListServiceClient(SampleTree());
        using var cache = new MetadataCache(service, "acct", "user@example.com", Opts());
        await cache.BuildAsync("/");
        await cache.EnsureSyncCursorAsync();
        service.SyncDeltas.Enqueue(MakeDelta("sync::p1", hasMore: true, adds: new[] { File("/A/x.txt") }));
        service.SyncDeltas.Enqueue(MakeDelta("sync::p2", hasMore: false, adds: new[] { File("/A/y.txt") }));

        var result = await cache.SyncAsync();

        result.Pages.Should().Be(2);
        cache.GetSyncState()!.Cursor.Should().Be("sync::p2");
        cache.TryGet("/A", out var entry).Should().BeTrue();
        entry!.Items.Select(i => i.Path).Should().Contain(new[] { "/A/x.txt", "/A/y.txt" });
    }

    [Fact]
    public async Task SyncAsync_ResetRequired_SignalsRebuild()
    {
        var service = new FakeListServiceClient(SampleTree());
        using var cache = new MetadataCache(service, "acct", "user@example.com", Opts());
        await cache.BuildAsync("/");
        await cache.EnsureSyncCursorAsync();
        service.SyncDeltas.Enqueue(new DropboxServiceClient.ListFolderDelta { ResetRequired = true });

        var result = await cache.SyncAsync();

        result.ResetRequired.Should().BeTrue();
    }

    [Fact]
    public async Task SyncAsync_NewFolderDelta_GetsOwnEntryForLaterChildren()
    {
        var service = new FakeListServiceClient(SampleTree());
        using var cache = new MetadataCache(service, "acct", "user@example.com", Opts());
        await cache.BuildAsync("/");
        await cache.EnsureSyncCursorAsync();
        service.SyncDeltas.Enqueue(MakeDelta("sync::p1", hasMore: true, adds: new[] { Folder("/A/C") }));
        service.SyncDeltas.Enqueue(MakeDelta("sync::p2", hasMore: false, adds: new[] { File("/A/C/leaf.txt") }));

        await cache.SyncAsync();

        cache.TryGet("/A/C", out var entry).Should().BeTrue();
        entry!.Items.Select(i => i.Path).Should().Contain("/A/C/leaf.txt");
    }

    private static DropboxServiceClient.ListFolderDelta MakeDelta(string newCursor, bool hasMore,
        IEnumerable<DropboxItem>? adds = null, IEnumerable<string>? removes = null)
    {
        var delta = new DropboxServiceClient.ListFolderDelta { NewCursor = newCursor, HasMore = hasMore };
        if (adds != null) delta.AddsOrUpdates.AddRange(adds);
        if (removes != null) delta.Removes.AddRange(removes);
        return delta;
    }
}
