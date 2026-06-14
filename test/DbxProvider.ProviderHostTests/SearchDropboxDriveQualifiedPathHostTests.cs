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
/// Behavior-first tests proving that <c>Search-Dropbox</c> emits items whose
/// <c>Path</c> is a drive-qualified provider path (<c>Dbx:\Folder\file</c>) so they
/// pipe straight into <c>Remove-Item</c> from any location -- the regression the
/// user hit was <c>Search-Dropbox ... | Remove-Item</c> failing with
/// "Cannot find path 'D:\...'" because the bare API path was rooted on the current
/// filesystem drive. These host PowerShell in-process against an in-memory fake
/// service (no Dropbox credentials needed).
/// </summary>
public class SearchDropboxDriveQualifiedPathHostTests : IDisposable
{
    private readonly string _cacheDir =
        Path.Combine(Path.GetTempPath(), "DbxDriveQualHostTests-" + Guid.NewGuid().ToString("N"));

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
# Stand on a filesystem location, NOT the Dbx drive, to prove location independence.
Set-Location ([System.IO.Path]::GetTempPath())
";

    // A zero-byte conflict file plus a normal file, both under /Temp.
    private static List<DropboxItem> Tree() => new()
    {
        new() { Name = "Temp", Path = "/Temp", IsFolder = true },
        new() { Name = "05mindmap (conflicted copy).svg", Path = "/Temp/05mindmap (conflicted copy).svg", IsFolder = false, Length = 0 },
        new() { Name = "notes.txt", Path = "/Temp/notes.txt", IsFolder = false, Length = 12 },
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
    public void SearchDropbox_EmitsDriveQualifiedPath_AndPreservesApiPath()
    {
        var fake = new FakeDropboxServiceClient(Tree());
        using var ps = NewHost(fake);
        ps.AddScript(Setup + "Search-Dropbox '*conflicted copy*'");
        var results = ps.Invoke();

        Assert.False(ps.HadErrors, Errors(ps));
        var item = Assert.Single(results);
        Assert.Equal(@"Dbx:\Temp\05mindmap (conflicted copy).svg", (string)item.Properties["Path"].Value);
        // The raw Dropbox API path is preserved for callers that need it.
        Assert.Equal("/Temp/05mindmap (conflicted copy).svg", (string)item.Properties["DropboxPath"].Value);
        Assert.Equal("05mindmap (conflicted copy).svg", (string)item.Properties["Name"].Value);
    }

    [Fact]
    public void SearchDropbox_PipedToRemoveItem_DeletesFromDropbox_FromFilesystemLocation()
    {
        var fake = new FakeDropboxServiceClient(Tree());
        using var ps = NewHost(fake);
        // The reported repro: from a filesystem drive, pipe search results to
        // Remove-Item. With a bare API path this failed with "Cannot find path
        // 'D:\...'"; with a drive-qualified path it routes to the Dropbox provider.
        ps.AddScript(Setup + "Search-Dropbox '*conflicted copy*' | Remove-Item");
        ps.Invoke();

        Assert.False(ps.HadErrors, Errors(ps));
        Assert.Equal(new[] { "/Temp/05mindmap (conflicted copy).svg" }, fake.Deletes.ToArray());
    }

    [Fact]
    public void RemoveDropboxItemBatch_StripsDriveQualifier_BeforeDeleting()
    {
        var fake = new FakeDropboxServiceClient(Tree());
        using var ps = NewHost(fake);
        // A drive-qualified path (Dbx:\...), as emitted by Search-Dropbox, must have
        // its qualifier stripped so the Dropbox API receives the bare /Temp/... path.
        // Passed as an explicit literal so this asserts the cmdlet's stripping
        // independently of how Search-Dropbox formats its output.
        ps.AddScript(Setup +
            @"Remove-DropboxItemBatch -Path 'Dbx:\Temp\05mindmap (conflicted copy).svg' -Confirm:$false");
        ps.Invoke();

        Assert.False(ps.HadErrors, Errors(ps));
        Assert.Equal(new[] { "/Temp/05mindmap (conflicted copy).svg" }, fake.BatchDeletes.ToArray());
    }

    [Fact]
    public void RemoveDropboxItemBatch_StillAcceptsBareApiPath()
    {
        var fake = new FakeDropboxServiceClient(Tree());
        using var ps = NewHost(fake);
        // Back-compat: a bare /Temp/... path (no drive qualifier) deletes unchanged.
        ps.AddScript(Setup +
            "Remove-DropboxItemBatch -Path '/Temp/notes.txt' -Confirm:$false");
        ps.Invoke();

        Assert.False(ps.HadErrors, Errors(ps));
        Assert.Equal(new[] { "/Temp/notes.txt" }, fake.BatchDeletes.ToArray());
    }
}
