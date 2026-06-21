using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Management.Automation;
using System.Text;
using IntelliTect.Dropbox;
using Xunit;

namespace DbxProvider.ProviderHostTests;

/// <summary>
/// Behavior-first tests for <c>Remove-DropboxItemBatch</c>: piping the
/// <c>DropboxItem</c> objects emitted by <c>Search-Dropbox</c> must delete them
/// (binding by path, not the object's <c>ToString()</c>); all piped inputs must
/// collapse into a single batch call; and entries the server could not delete
/// (for example an already-deleted path) must surface as errors instead of
/// silent successes. Hosts PowerShell in-process against an in-memory fake
/// service (no Dropbox credentials needed).
/// </summary>
public class RemoveDropboxItemBatchHostTests : IDisposable
{
    private readonly string _cacheDir =
        Path.Combine(Path.GetTempPath(), "DbxRemoveBatchHostTests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { if (Directory.Exists(_cacheDir)) Directory.Delete(_cacheDir, recursive: true); }
        catch { /* best effort */ }
    }

    private const string Setup = @"
$ErrorActionPreference = 'Stop'
Import-Module $dllPath
$prov = Get-PSProvider Dropbox
$psdrive = [System.Management.Automation.PSDriveInfo]::new('Dbx', $prov, '\', 'fake', $null)
$dbx = [DbxProvider.Provider.DropboxDriveInfo]::new($psdrive, $fake)
$opts = [IntelliTect.Dropbox.CacheOptions]::new()
$opts.RootDirectoryOverride = $cacheDir
$dbx.InitializeCache('fake-account', 'fake@example.com', $opts)
$ExecutionContext.SessionState.Drive.New($dbx, 'global') | Out-Null
Build-DropboxCache -Path '/' | Out-Null
Set-Location ([System.IO.Path]::GetTempPath())
";

    private static List<DropboxItem> Tree() => new()
    {
        new() { Name = "Temp", Path = "/Temp", IsFolder = true },
        new() { Name = "a (conflicted copy).svg", Path = "/Temp/a (conflicted copy).svg", IsFolder = false, Length = 0 },
        new() { Name = "b (conflicted copy).svg", Path = "/Temp/b (conflicted copy).svg", IsFolder = false, Length = 0 },
    };

    private PowerShell NewHost(FakeDropboxServiceClient fake)
    {
        var ps = PowerShell.Create();
        ps.Runspace.SessionStateProxy.SetVariable("fake", fake);
        ps.Runspace.SessionStateProxy.SetVariable("dllPath",
            Path.Combine(AppContext.BaseDirectory, "DbxProvider.dll"));
        ps.Runspace.SessionStateProxy.SetVariable("cacheDir", _cacheDir);
        return ps;
    }

    private static string Errors(PowerShell ps)
    {
        var sb = new StringBuilder();
        foreach (var e in ps.Streams.Error) sb.AppendLine(e.ToString());
        return sb.ToString();
    }

    [Fact]
    public void SuccessfulBatchDelete_RemovesItemsFromCache()
    {
        // After a batch delete, a cache-mode Search-Dropbox must not still return
        // the deleted item: the cmdlet must apply the removal to the local cache
        // (mirroring Remove-Item) so choosing the batch cmdlet never yields stale
        // results.
        var fake = new FakeDropboxServiceClient(Tree());
        using var ps = NewHost(fake);
        ps.AddScript(Setup +
            "'/Temp/a (conflicted copy).svg' | Remove-DropboxItemBatch -Confirm:$false; " +
            "(Search-Dropbox '*conflicted copy*' | ForEach-Object { $_.Path }) -join '|'");
        var results = ps.Invoke();

        Assert.False(ps.HadErrors, Errors(ps));
        var joined = results[results.Count - 1]?.ToString() ?? "";
        Assert.DoesNotContain("a (conflicted copy).svg", joined);
        // The other conflict file is untouched and still found.
        Assert.Contains("b (conflicted copy).svg", joined);
    }

    [Fact]
    public void SkipCacheUpdate_LeavesCacheEntriesIntact()
    {
        // With -SkipCacheUpdate the cache is intentionally left stale, so a
        // cache-mode search still lists the (server-side deleted) item.
        var fake = new FakeDropboxServiceClient(Tree());
        using var ps = NewHost(fake);
        ps.AddScript(Setup +
            "'/Temp/a (conflicted copy).svg' | Remove-DropboxItemBatch -SkipCacheUpdate -Confirm:$false; " +
            "(Search-Dropbox '*conflicted copy*' | ForEach-Object { $_.Path }) -join '|'");
        var results = ps.Invoke();

        Assert.False(ps.HadErrors, Errors(ps));
        var joined = results[results.Count - 1]?.ToString() ?? "";
        Assert.Contains("a (conflicted copy).svg", joined);
    }

    [Fact]
    public void PipedDropboxItems_BindByPath_AndDeleteInSingleBatch()
    {
        var fake = new FakeDropboxServiceClient(Tree());
        using var ps = NewHost(fake);
        // The DropboxItem objects (not their .Path strings) are piped directly.
        // Their ToString() is "[F] <name>", so this only works if the cmdlet binds
        // each object's path rather than coercing the object to a string.
        ps.AddScript(Setup + "Search-Dropbox '*conflicted copy*' | Remove-DropboxItemBatch -Confirm:$false");
        ps.Invoke();

        Assert.False(ps.HadErrors, Errors(ps));
        Assert.Equal(
            new[] { "/Temp/a (conflicted copy).svg", "/Temp/b (conflicted copy).svg" },
            fake.BatchDeletes.OrderBy(p => p, StringComparer.Ordinal).ToArray());
        // Two piped items, but a single batch API call.
        Assert.Equal(1, fake.BatchInvocations);
    }

    [Fact]
    public void MultiplePipedPaths_DeletedInSingleBatch()
    {
        var fake = new FakeDropboxServiceClient(Tree());
        using var ps = NewHost(fake);
        ps.AddScript(Setup +
            "'/Temp/a (conflicted copy).svg','/Temp/b (conflicted copy).svg' | Remove-DropboxItemBatch -Confirm:$false");
        ps.Invoke();

        Assert.False(ps.HadErrors, Errors(ps));
        Assert.Equal(2, fake.BatchDeletes.Count);
        Assert.Equal(1, fake.BatchInvocations);
    }

    [Fact]
    public void MoreThanOneThousandPaths_AreChunkedAcrossMultipleBatches()
    {
        // Dropbox's delete_batch endpoint caps at 1000 entries per call; sending
        // more in one request fails ("Error while copying content to a stream").
        // The cmdlet must split the accumulated paths into <= 1000-entry batches.
        var tree = new List<DropboxItem> { new() { Name = "Temp", Path = "/Temp", IsFolder = true } };
        const int count = 1001;
        for (int i = 0; i < count; i++)
        {
            tree.Add(new() { Name = $"f{i}.txt", Path = $"/Temp/f{i}.txt", IsFolder = false, Length = 0 });
        }
        var fake = new FakeDropboxServiceClient(tree);
        using var ps = NewHost(fake);
        ps.AddScript(Setup +
            "0..1000 | ForEach-Object { \"/Temp/f$_.txt\" } | Remove-DropboxItemBatch -Confirm:$false");
        ps.Invoke();

        Assert.False(ps.HadErrors, Errors(ps));
        Assert.Equal(count, fake.BatchDeletes.Count);
        // 1001 paths must span at least two batch calls (no single oversized request).
        Assert.True(fake.BatchInvocations >= 2,
            $"Expected chunked batches but saw {fake.BatchInvocations} invocation(s).");
    }

    [Fact]
    public void BatchSize_ControlsApiCallChunking_IndependentOfConcurrency()
    {
        // -BatchSize sets how many paths go in each delete_batch call, decoupled
        // from -MaxConcurrency. Smaller batches finish (and advance the progress
        // bar) more often. Six paths at -BatchSize 2 must produce three API calls.
        // Reverting to the hardcoded 1000-entry chunk size collapses these into a
        // single call, so this fails for a behavioral reason.
        var tree = new List<DropboxItem> { new() { Name = "Temp", Path = "/Temp", IsFolder = true } };
        for (int i = 0; i < 6; i++)
        {
            tree.Add(new() { Name = $"c{i}.txt", Path = $"/Temp/c{i}.txt", IsFolder = false, Length = 0 });
        }
        var fake = new FakeDropboxServiceClient(tree);
        using var ps = NewHost(fake);
        ps.AddScript(Setup +
            "0..5 | ForEach-Object { \"/Temp/c$_.txt\" } | " +
            "Remove-DropboxItemBatch -BatchSize 2 -MaxConcurrency 1 -Confirm:$false");
        ps.Invoke();

        Assert.False(ps.HadErrors, Errors(ps));
        Assert.Equal(6, fake.BatchDeletes.Count);
        Assert.Equal(3, fake.BatchInvocations);
    }

    [Fact]
    public void AlreadyDeletedPath_SurfacesNonTerminatingError()
    {
        var fake = new FakeDropboxServiceClient(Tree());
        using var ps = NewHost(fake);
        // A path that is not present must be reported as a failure, not a silent
        // success. Use Continue so the non-terminating error is recorded, not thrown.
        ps.AddScript(Setup +
            "$ErrorActionPreference='Continue'; Remove-DropboxItemBatch -Path '/Temp/missing.txt' -Confirm:$false");
        ps.Invoke();

        Assert.True(ps.HadErrors);
        var combined = Errors(ps);
        Assert.Contains("Could not delete", combined);
        Assert.Contains("/Temp/missing.txt", combined);
    }

    [Fact]
    public void ConcurrentBatchDelete_StreamsProgressRecords()
    {
        // With -MaxConcurrency > 1 the cmdlet runs delete batches concurrently and
        // must stream a live progress bar so the multi-minute server-side wait is
        // never silent. Reverting to a plain blocking call (no WriteProgress) emits
        // zero progress records, so this fails for a behavioral reason.
        var fake = new FakeDropboxServiceClient(Tree());
        using var ps = NewHost(fake);
        ps.AddScript(Setup +
            "'/Temp/a (conflicted copy).svg','/Temp/b (conflicted copy).svg' | " +
            "Remove-DropboxItemBatch -MaxConcurrency 2 -Confirm:$false");
        ps.Invoke();

        Assert.False(ps.HadErrors, Errors(ps));
        var progress = ps.Streams.Progress
            .Where(p => p.Activity == "Removing Dropbox items")
            .ToList();
        Assert.NotEmpty(progress);
        // The processed counter must advance to the full count via the fine-grained
        // per-attempt callback (not only when the whole chunk finishes), so progress is
        // observable during the wait. Reverting that callback leaves the count at 0.
        Assert.Contains(progress, p => p.StatusDescription.Contains("2/2 processed"));
        // The bar must be closed out so it does not linger on the console.
        Assert.Contains(progress, p => p.RecordType == ProgressRecordType.Completed);
    }
}
