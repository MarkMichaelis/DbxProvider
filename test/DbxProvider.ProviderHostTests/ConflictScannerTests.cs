using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using IntelliTect.Dropbox;
using Xunit;

namespace DbxProvider.ProviderHostTests;

/// <summary>
/// Behavior-first tests for <see cref="ConflictScanner"/>. They drive the
/// scanner against <see cref="FakeDropboxServiceClient"/>, which scripts the
/// recursive-listing and delta-continue paths and counts how often each is
/// invoked, proving that warm runs prune the full enumeration.
/// </summary>
public class ConflictScannerTests
{
    private const string ConflictPattern = "*'s conflicted copy*";

    private static DropboxItem Conflict(string path, ulong bytes = 0) =>
        new() { Name = path.Split('/').Last(), Path = path, IsFolder = false, Length = bytes };

    private static DropboxItem File(string path, ulong bytes = 10) =>
        new() { Name = path.Split('/').Last(), Path = path, IsFolder = false, Length = bytes };

    private static ConflictScanParameters Params(string startPath = "", string pattern = ConflictPattern, bool includeNonZero = false) =>
        new() { StartPath = startPath, Pattern = pattern, IncludeNonZero = includeNonZero };

    [Fact]
    public async Task ColdRun_FullEnumeration_FindsZeroByteMatches_AndSavesCursor()
    {
        var items = new List<DropboxItem>
        {
            File("/A/normal.txt"),
            Conflict("/A/report's conflicted copy 2024.docx"),
            File("/A/big's conflicted copy.bin", bytes: 500), // non-zero -> excluded by default
        };
        var fake = new FakeDropboxServiceClient(items);
        fake.SetFullCursor("cursor-1");
        var scanner = new ConflictScanner(fake);

        var result = await scanner.ScanAsync(Params(), previousState: null);

        Assert.True(result.WasFullScan);
        Assert.Equal(1, fake.FullListCalls);
        var match = Assert.Single(result.Matches);
        Assert.Equal("/A/report's conflicted copy 2024.docx", match.Path);
        Assert.Equal("cursor-1", result.State.Cursor);
    }

    [Fact]
    public async Task WarmRun_AddedConflict_PickedUpViaDelta_WithoutFullReEnumeration()
    {
        var fake = new FakeDropboxServiceClient(new List<DropboxItem> { File("/A/normal.txt") });
        var scanner = new ConflictScanner(fake);
        var cold = await scanner.ScanAsync(Params(), previousState: null);
        Assert.Equal(1, fake.FullListCalls);

        // The new conflict file arrives as a delta add, not a full listing.
        var delta = new DropboxServiceClient.ListFolderDelta { NewCursor = "cursor-2", HasMore = false };
        delta.AddsOrUpdates.Add(Conflict("/A/new's conflicted copy.txt"));
        fake.EnqueueDelta(delta);

        var warm = await scanner.ScanAsync(Params(), previousState: cold.State);

        Assert.False(warm.WasFullScan);
        Assert.Equal(1, fake.FullListCalls);   // NOT re-enumerated
        Assert.Equal(1, fake.ContinueCalls);
        Assert.Contains(warm.Matches, m => m.Path == "/A/new's conflicted copy.txt");
        Assert.Equal("cursor-2", warm.State.Cursor);
    }

    [Fact]
    public async Task WarmRun_DeltaRemove_DropsPreviouslyFoundMatch()
    {
        var existing = "/A/old's conflicted copy.txt";
        var fake = new FakeDropboxServiceClient(new List<DropboxItem> { Conflict(existing) });
        var scanner = new ConflictScanner(fake);
        var cold = await scanner.ScanAsync(Params(), previousState: null);
        Assert.Single(cold.Matches);

        var delta = new DropboxServiceClient.ListFolderDelta { NewCursor = "cursor-2", HasMore = false };
        delta.Removes.Add(existing.ToLowerInvariant());
        fake.EnqueueDelta(delta);

        var warm = await scanner.ScanAsync(Params(), previousState: cold.State);

        Assert.Empty(warm.Matches);
    }

    [Fact]
    public async Task WarmRun_NonConflictUpdate_RemovesPreviouslyMatchedPath()
    {
        var path = "/A/grew's conflicted copy.txt";
        var fake = new FakeDropboxServiceClient(new List<DropboxItem> { Conflict(path) });
        var scanner = new ConflictScanner(fake);
        var cold = await scanner.ScanAsync(Params(), previousState: null);
        Assert.Single(cold.Matches);

        // Same path, now non-zero -> no longer satisfies the default criteria.
        var delta = new DropboxServiceClient.ListFolderDelta { NewCursor = "cursor-2", HasMore = false };
        delta.AddsOrUpdates.Add(File(path, bytes: 1024));
        fake.EnqueueDelta(delta);

        var warm = await scanner.ScanAsync(Params(), previousState: cold.State);

        Assert.Empty(warm.Matches);
    }

    [Fact]
    public async Task WarmRun_ResetRequired_FallsBackToFullScan()
    {
        var fake = new FakeDropboxServiceClient(new List<DropboxItem>
        {
            Conflict("/A/recovered's conflicted copy.txt"),
        });
        var scanner = new ConflictScanner(fake);
        var cold = await scanner.ScanAsync(Params(), previousState: null);
        Assert.Equal(1, fake.FullListCalls);

        fake.EnqueueDelta(new DropboxServiceClient.ListFolderDelta { ResetRequired = true });

        var warm = await scanner.ScanAsync(Params(), previousState: cold.State);

        Assert.True(warm.WasFullScan);
        Assert.Equal(2, fake.FullListCalls); // fell back to a fresh full enumeration
        Assert.Contains(warm.Matches, m => m.Path == "/A/recovered's conflicted copy.txt");
    }

    [Fact]
    public async Task ParamChange_ForcesFullPass_NotIncremental()
    {
        var fake = new FakeDropboxServiceClient(new List<DropboxItem>
        {
            Conflict("/A/x's conflicted copy.txt"),
        });
        var scanner = new ConflictScanner(fake);
        var cold = await scanner.ScanAsync(Params(pattern: ConflictPattern), previousState: null);
        Assert.Equal(1, fake.FullListCalls);

        // Any continue call would prove an (incorrect) incremental attempt.
        fake.EnqueueDelta(new DropboxServiceClient.ListFolderDelta { NewCursor = "x", HasMore = false });

        var second = await scanner.ScanAsync(Params(pattern: "*DIFFERENT*"), previousState: cold.State);

        Assert.True(second.WasFullScan);
        Assert.Equal(2, fake.FullListCalls);
        Assert.Equal(0, fake.ContinueCalls); // never went incremental
    }
}