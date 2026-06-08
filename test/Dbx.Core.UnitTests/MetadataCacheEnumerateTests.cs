using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using IntelliTect.Dropbox;
using Microsoft.Data.Sqlite;
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

    [Fact]
    public async Task EnumerateItems_SkipsRowWithCorruptJson_WithoutThrowing()
    {
        string dbPath;
        {
            var service = new FakeListServiceClient(SampleTree());
            using var cache = new MetadataCache(service, "acct", "user@example.com", Opts());
            await cache.BuildAsync("/");
            dbPath = cache.DatabasePath;
        } // dispose flushes and closes the connection so the row can be corrupted

        // Replace the '/A' entry's stored item list with non-JSON text.
        var connectionString = new SqliteConnectionStringBuilder { DataSource = dbPath }.ToString();
        using (var conn = new SqliteConnection(connectionString))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE entries SET items_json = $bad WHERE items_json LIKE '%file2.txt%';";
            cmd.Parameters.AddWithValue("$bad", "this is not json");
            cmd.ExecuteNonQuery().Should().Be(1);
        }

        // A fresh cache over the same database reads the corrupt row from disk.
        var service2 = new FakeListServiceClient(SampleTree());
        using var reopened = new MetadataCache(service2, "acct", "user@example.com", Opts());

        var items = reopened.EnumerateItems().Select(i => i.Path).ToList();

        // The corrupt '/A' row is skipped (its children are gone) but the valid
        // '/A/B' entry still yields its item -- and nothing throws.
        items.Should().Contain("/A/B/file.txt");
        items.Should().NotContain("/A/file2.txt");
    }

    [Fact]
    public async Task FindItems_EmptyPathItems_AreNotCollapsedByDedup()
    {
        string dbPath;
        {
            var service = new FakeListServiceClient(SampleTree());
            using var cache = new MetadataCache(service, "acct", "user@example.com", Opts());
            await cache.BuildAsync("/");
            dbPath = cache.DatabasePath;
        }

        // Two distinct items that both have an empty Path (as a malformed row
        // could yield). De-duplication by path must not collapse them into one.
        var twoEmptyPath =
            @"[{""path"":"""",""name"":""dup1"",""isFolder"":false,""length"":0}," +
            @"{""path"":"""",""name"":""dup2"",""isFolder"":false,""length"":0}]";
        var connectionString = new SqliteConnectionStringBuilder { DataSource = dbPath }.ToString();
        using (var conn = new SqliteConnection(connectionString))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE entries SET items_json = $j WHERE items_json LIKE '%file2.txt%';";
            cmd.Parameters.AddWithValue("$j", twoEmptyPath);
            cmd.ExecuteNonQuery().Should().Be(1);
        }

        var service2 = new FakeListServiceClient(SampleTree());
        using var reopened = new MetadataCache(service2, "acct", "user@example.com", Opts());

        var matches = reopened.FindItems(i => i.Name == "dup1" || i.Name == "dup2");

        matches.Select(i => i.Name).Should().BeEquivalentTo(new[] { "dup1", "dup2" });
    }
}