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
/// Behavior-first tests for recursive <c>Get-ChildItem</c> (issue #83): the
/// enumeration streams from the local metadata cache (not the recursive list API)
/// and yields items per directory -- each directory's sub-folders first, then its
/// files -- instead of buffering the whole subtree and applying a single global
/// folders-first sort.
/// </summary>
public class ProviderRecurseStreamHostTests : IDisposable
{
    private readonly string _cacheDir =
        Path.Combine(Path.GetTempPath(), "DbxRecurseStreamTests-" + Guid.NewGuid().ToString("N"));

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

    // A subtree where per-directory order differs from a single global folders-first
    // sort: globally the files would order aaa.txt before zfile.txt, but
    // per-directory the root's own zfile.txt precedes the nested Mdir/aaa.txt.
    private static List<DropboxItem> Tree() => new()
    {
        new() { Name = "Root", Path = "/Root", IsFolder = true },
        new() { Name = "Mdir", Path = "/Root/Mdir", IsFolder = true },
        new() { Name = "aaa.txt", Path = "/Root/Mdir/aaa.txt", IsFolder = false, Length = 3 },
        new() { Name = "zfile.txt", Path = "/Root/zfile.txt", IsFolder = false, Length = 3 },
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
    public void GetChildItem_Recurse_YieldsPerDirectoryOrder_SubFoldersThenFiles()
    {
        var fake = new FakeDropboxServiceClient(Tree());
        using var ps = NewHost(fake);
        ps.AddScript(Setup + "Get-ChildItem -LiteralPath 'Dbx:\\Root' -Recurse");
        var results = ps.Invoke();

        Assert.False(ps.HadErrors, Errors(ps));
        var order = results.Select(r => (string)r.Properties["DropboxPath"].Value).ToList();
        Assert.Equal(
            new[] { "/Root/Mdir", "/Root/zfile.txt", "/Root/Mdir/aaa.txt" },
            order);
    }

    [Fact]
    public void GetChildItem_Recurse_StreamsFromCache_DoesNotCallRecursiveListApi()
    {
        var fake = new FakeDropboxServiceClient(Tree());
        using var ps = NewHost(fake);
        ps.AddScript(Setup + "Get-ChildItem -LiteralPath 'Dbx:\\Root' -Recurse | Out-Null");
        ps.Invoke();

        Assert.False(ps.HadErrors, Errors(ps));
        Assert.Equal(0, fake.RecursiveListFolderCalls);
    }
}
