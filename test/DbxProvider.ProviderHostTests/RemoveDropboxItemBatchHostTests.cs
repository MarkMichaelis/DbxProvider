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
}
