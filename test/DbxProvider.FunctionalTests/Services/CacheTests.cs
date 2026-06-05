using DbxProvider.FunctionalTests.Infrastructure;
using IntelliTect.Dropbox;
using Xunit;

namespace DbxProvider.FunctionalTests.Services;

[Collection("Dropbox")]
public class CacheTests : IDisposable
{
    private readonly DropboxFixture _fixture;
    private readonly string _tempCacheRoot;

    public CacheTests(DropboxFixture fixture)
    {
        _fixture = fixture;
        _tempCacheRoot = Path.Combine(Path.GetTempPath(), "DbxProviderCacheTests-" + Guid.NewGuid().ToString("N"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempCacheRoot))
        {
            try { Directory.Delete(_tempCacheRoot, recursive: true); } catch { }
        }
    }

    private MetadataCache NewCache(string accountId = "test-account-001", CacheOptions? opts = null)
    {
        opts ??= new CacheOptions { RootDirectoryOverride = _tempCacheRoot, FlushIntervalSeconds = 0 };
        opts.RootDirectoryOverride ??= _tempCacheRoot;
        return new MetadataCache(_fixture.Service!, accountId, opts);
    }

    /// <summary>Create a test folder. Rate-limit / soft-throttle retries are
    /// handled by the provider's <c>RateLimitRetry</c> helper; the only thing
    /// that still needs handling here is the brief replication delay before a
    /// subsequent <c>list_folder</c> sees the new folder.</summary>
    private async Task<string> NewTestFolderWithRetryAsync(string testName)
    {
        var path = await _fixture.NewTestFolderAsync(testName);
        // Small consistency wait so a subsequent list_folder doesn't hit
        // path/not_found while replication catches up.
        await Task.Delay(300);
        return path;
    }

    /// <summary>list_folder can briefly return path/not_found right after a folder
    /// is created. Retry a few times before giving up.</summary>
    private static async Task<T> RetryOnNotFoundAsync<T>(Func<Task<T>> action)
    {
        for (int attempt = 0; ; attempt++)
        {
            try { return await action(); }
            catch (Dropbox.Api.ApiException<Dropbox.Api.Files.ListFolderError> ex)
                when (attempt < 3 && ex.ErrorResponse.IsPath)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(500 * (attempt + 1)));
            }
        }
    }

    private Task CreateFolderWithRetryAsync(string path) =>
        _fixture.Service!.CreateFolderAsync(path);

    [SkippableFact]
    public async Task Cache_ColdMiss_PopulatesEntry()
    {
        TestSkip.IfUnavailable(_fixture);
        var folder = await NewTestFolderWithRetryAsync(nameof(Cache_ColdMiss_PopulatesEntry));
        try
        {
            using var cache = NewCache();
            await using var _ = new Cleanup(() => Task.CompletedTask);

            var account = await _fixture.Service!.GetCurrentAccountAsync();
            using var realCache = NewCache(account.AccountId);

            var items = await realCache.GetChildrenAsync(folder);
            Assert.Empty(items);

            Assert.True(realCache.TryGet(folder, out var entry));
            Assert.NotNull(entry);
            Assert.False(string.IsNullOrEmpty(entry!.Cursor));
            Assert.Equal(1, realCache.Count);
        }
        finally
        {
            try { await _fixture.Service!.DeleteAsync(folder); } catch { }
        }
    }

    [SkippableFact]
    public async Task Cache_DetectsExternalAdd()
    {
        TestSkip.IfUnavailable(_fixture);
        var folder = await NewTestFolderWithRetryAsync(nameof(Cache_DetectsExternalAdd));
        try
        {
            using var cache = NewCache();
            var first = await cache.GetChildrenAsync(folder);
            Assert.Empty(first);

            // Mutate via the raw service (bypassing the cache).
            using var ms = new MemoryStream(System.Text.Encoding.UTF8.GetBytes("hello"));
            await _fixture.Service!.UploadAsync($"{folder}/added.txt", ms);

            var second = await cache.GetChildrenAsync(folder);
            Assert.Contains(second, i => i.Name == "added.txt");
        }
        finally
        {
            try { await _fixture.Service!.DeleteAsync(folder); } catch { }
        }
    }

    [SkippableFact]
    public async Task Cache_DetectsExternalDelete()
    {
        TestSkip.IfUnavailable(_fixture);
        var folder = await NewTestFolderWithRetryAsync(nameof(Cache_DetectsExternalDelete));
        try
        {
            using (var ms = new MemoryStream(System.Text.Encoding.UTF8.GetBytes("x")))
                await _fixture.Service!.UploadAsync($"{folder}/doomed.txt", ms);

            using var cache = NewCache();
            var first = await cache.GetChildrenAsync(folder);
            Assert.Contains(first, i => i.Name == "doomed.txt");

            await _fixture.Service!.DeleteAsync($"{folder}/doomed.txt");

            var second = await cache.GetChildrenAsync(folder);
            Assert.DoesNotContain(second, i => i.Name == "doomed.txt");
        }
        finally
        {
            try { await _fixture.Service!.DeleteAsync(folder); } catch { }
        }
    }

    [SkippableFact]
    public async Task Cache_DetectsRename()
    {
        TestSkip.IfUnavailable(_fixture);
        var folder = await NewTestFolderWithRetryAsync(nameof(Cache_DetectsRename));
        try
        {
            using (var ms = new MemoryStream(System.Text.Encoding.UTF8.GetBytes("x")))
                await _fixture.Service!.UploadAsync($"{folder}/old.txt", ms);

            using var cache = NewCache();
            var first = await cache.GetChildrenAsync(folder);
            Assert.Contains(first, i => i.Name == "old.txt");

            await _fixture.Service!.MoveAsync($"{folder}/old.txt", $"{folder}/new.txt");

            var second = await cache.GetChildrenAsync(folder);
            Assert.DoesNotContain(second, i => i.Name == "old.txt");
            Assert.Contains(second, i => i.Name == "new.txt");
        }
        finally
        {
            try { await _fixture.Service!.DeleteAsync(folder); } catch { }
        }
    }

    [SkippableFact]
    public async Task Cache_HandlesCursorReset()
    {
        TestSkip.IfUnavailable(_fixture);
        var folder = await NewTestFolderWithRetryAsync(nameof(Cache_HandlesCursorReset));
        try
        {
            using var cache = NewCache();
            await cache.GetChildrenAsync(folder);
            Assert.True(cache.TryGet(folder, out var entry));

            // Force a reset by injecting an obviously-invalid cursor.
            entry!.Cursor = "AAAAAAAAAAAAAAAA-invalid-cursor-AAAAAAAAAAAAAAAA";

            using (var ms = new MemoryStream(System.Text.Encoding.UTF8.GetBytes("x")))
                await _fixture.Service!.UploadAsync($"{folder}/after-reset.txt", ms);

            var items = await cache.GetChildrenAsync(folder);
            Assert.Contains(items, i => i.Name == "after-reset.txt");
            Assert.True(cache.TryGet(folder, out var refreshed));
            Assert.NotEqual("AAAAAAAAAAAAAAAA-invalid-cursor-AAAAAAAAAAAAAAAA", refreshed!.Cursor);
        }
        finally
        {
            try { await _fixture.Service!.DeleteAsync(folder); } catch { }
        }
    }

    [SkippableFact]
    public async Task Cache_DiskRoundTrip()
    {
        TestSkip.IfUnavailable(_fixture);
        var folder = await NewTestFolderWithRetryAsync(nameof(Cache_DiskRoundTrip));
        try
        {
            using (var ms = new MemoryStream(System.Text.Encoding.UTF8.GetBytes("x")))
                await _fixture.Service!.UploadAsync($"{folder}/persisted.txt", ms);

            // First instance populates and flushes.
            using (var cache = NewCache(accountId: "round-trip-account"))
            {
                await cache.GetChildrenAsync(folder);
                cache.Flush();
            }

            // Second instance hydrates from disk.
            using var cache2 = NewCache(accountId: "round-trip-account");
            Assert.True(cache2.TryGet(folder, out var entry));
            Assert.NotNull(entry);
            Assert.Contains(entry!.Items, i => i.Name == "persisted.txt");
        }
        finally
        {
            try { await _fixture.Service!.DeleteAsync(folder); } catch { }
        }
    }

    [SkippableFact]
    public async Task Cache_AccountIdScoping()
    {
        TestSkip.IfUnavailable(_fixture);
        var folder = await NewTestFolderWithRetryAsync(nameof(Cache_AccountIdScoping));
        try
        {
            using var a = NewCache(accountId: "account-A");
            using var b = NewCache(accountId: "account-B");
            await a.GetChildrenAsync(folder);
            a.Flush();

            Assert.NotEqual(a.AccountIdHash, b.AccountIdHash);
            Assert.NotEqual(a.AccountDirectory, b.AccountDirectory);

            // B has no entry, A does.
            Assert.False(b.TryGet(folder, out _));
            Assert.True(a.TryGet(folder, out _));
        }
        finally
        {
            try { await _fixture.Service!.DeleteAsync(folder); } catch { }
        }
    }

    [SkippableFact]
    public async Task Cache_OverBudget_SpillsToDiskWithoutLoss()
    {
        TestSkip.IfUnavailable(_fixture);
        var root = await NewTestFolderWithRetryAsync(nameof(Cache_OverBudget_SpillsToDiskWithoutLoss));
        try
        {
            // Create three subfolders so we have three cacheable paths.
            for (int i = 0; i < 3; i++)
                await CreateFolderWithRetryAsync($"{root}/sub{i}");

            var opts = new CacheOptions
            {
                RootDirectoryOverride = _tempCacheRoot,
                FlushIntervalSeconds = 0,
                MaxInMemoryEntries = 2
            };
            using var cache = new MetadataCache(_fixture.Service!, "spill-account", opts);

            await cache.GetChildrenAsync($"{root}/sub0");
            await Task.Delay(10);
            await cache.GetChildrenAsync($"{root}/sub1");
            await Task.Delay(10);
            await cache.GetChildrenAsync($"{root}/sub2");

            // The in-memory working set is bounded by the budget...
            Assert.Equal(2, cache.Count);
            // ...the least-recently-used entry was spilled out of memory...
            Assert.DoesNotContain(cache.Snapshot(), e => e.Path == $"{root}/sub0");

            // ...but the persistent cache is uncapped: after flushing the
            // resident entries, all three survive on disk.
            cache.Flush();
            Assert.Equal(3, cache.PersistedCount());

            // The spilled entry is transparently re-hydrated from disk.
            Assert.True(cache.TryGet($"{root}/sub0", out var revived));
            Assert.NotNull(revived);
        }
        finally
        {
            try { await _fixture.Service!.DeleteAsync(root); } catch { }
        }
    }

    [SkippableFact]
    public async Task Cache_DisabledShortCircuits()
    {
        TestSkip.IfUnavailable(_fixture);
        var folder = await NewTestFolderWithRetryAsync(nameof(Cache_DisabledShortCircuits));
        try
        {
            // Brief wait for Dropbox propagation before listing.
            await Task.Delay(500);

            var opts = new CacheOptions
            {
                RootDirectoryOverride = _tempCacheRoot,
                FlushIntervalSeconds = 0,
                Enabled = false
            };
            using var cache = new MetadataCache(_fixture.Service!, "disabled-account", opts);

            await RetryOnNotFoundAsync(async () => { await cache.GetChildrenAsync(folder); return 0; });
            await cache.GetChildrenAsync(folder);

            Assert.Equal(0, cache.Count);
            Assert.False(cache.TryGet(folder, out _));
        }
        finally
        {
            try { await _fixture.Service!.DeleteAsync(folder); } catch { }
        }
    }

    [SkippableFact]
    public async Task Cache_WriteThrough_Add()
    {
        TestSkip.IfUnavailable(_fixture);
        var folder = await NewTestFolderWithRetryAsync(nameof(Cache_WriteThrough_Add));
        try
        {
            using var cache = NewCache();
            await cache.GetChildrenAsync(folder);

            // Simulate provider's write-through after a local mutation.
            var item = new IntelliTect.Dropbox.DropboxItem
            {
                Name = "local-add.txt",
                Path = $"{folder}/local-add.txt",
                IsFolder = false,
                Length = 5
            };
            cache.ApplyLocalAdd(item);

            Assert.True(cache.TryGet(folder, out var entry));
            Assert.Contains(entry!.Items, i => i.Name == "local-add.txt");
        }
        finally
        {
            try { await _fixture.Service!.DeleteAsync(folder); } catch { }
        }
    }

    private sealed class Cleanup : IAsyncDisposable
    {
        private readonly Func<Task> _action;
        public Cleanup(Func<Task> action) => _action = action;
        public ValueTask DisposeAsync() => new(_action());
    }
}


