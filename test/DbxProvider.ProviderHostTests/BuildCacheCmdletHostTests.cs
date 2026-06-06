using System;
using System.Collections.Generic;
using System.IO;
using System.Management.Automation;
using System.Text;
using IntelliTect.Dropbox;
using Xunit;

namespace DbxProvider.ProviderHostTests;

/// <summary>
/// Functional tests that run the real <c>Build-DropboxCache</c> cmdlet in-process
/// against a Dropbox drive backed by the in-memory fake, proving the cmdlet wires
/// the page-by-page build and the optional revision pass end-to-end and emits the
/// expected summary.
/// </summary>
public class BuildCacheCmdletHostTests
{
    private const string Script = @"
$ErrorActionPreference = 'Stop'
Import-Module $dllPath
$prov = Get-PSProvider Dropbox
$psdrive = [System.Management.Automation.PSDriveInfo]::new('Dbx', $prov, '\', 'fake', $null)
$dbx = [DbxProvider.Provider.DropboxDriveInfo]::new($psdrive, $fake)
$opts = [IntelliTect.Dropbox.CacheOptions]::new()
$opts.RootDirectoryOverride = $cacheDir
$dbx.InitializeCache('fake-account', 'fake@example.com', $opts)
$ExecutionContext.SessionState.Drive.New($dbx, 'global') | Out-Null

if ($includeRevisions) {
    $summary = Build-DropboxCache -Path '/' -IncludeRevisions
} else {
    $summary = Build-DropboxCache -Path '/'
}
[pscustomobject]@{
    FoldersCached            = [int]$summary.FoldersCached
    ItemsFound               = [int]$summary.ItemsFound
    FilesWithRevisionsCached = [int]$summary.FilesWithRevisionsCached
    RevisionsCached          = [int]$summary.RevisionsCached
}
";

    private static List<DropboxItem> SampleTree() => new()
    {
        new() { Name = "A", Path = "/A", IsFolder = true },
        new() { Name = "file2.txt", Path = "/A/file2.txt", IsFolder = false, Length = 5 },
        new() { Name = "B", Path = "/A/B", IsFolder = true },
        new() { Name = "file.txt", Path = "/A/B/file.txt", IsFolder = false, Length = 7 },
    };

    [Fact]
    public void BuildDropboxCache_EmitsSummary_WithFolderAndItemCounts()
    {
        var result = RunBuild(includeRevisions: false, fake => { });

        Assert.Equal(3, (int)result.Properties["FoldersCached"].Value);
        Assert.Equal(4, (int)result.Properties["ItemsFound"].Value);
        Assert.Equal(0, (int)result.Properties["RevisionsCached"].Value);
    }

    [Fact]
    public void BuildDropboxCache_WithIncludeRevisions_CachesRevisionsAndReportsCounts()
    {
        var result = RunBuild(includeRevisions: true, fake =>
        {
            fake.RevisionsByPath["/A/file2.txt"] = new List<DropboxRevision>
            {
                new() { Rev = "f2-r1", Length = 5 },
            };
            fake.RevisionsByPath["/A/B/file.txt"] = new List<DropboxRevision>
            {
                new() { Rev = "f-r1", Length = 7 },
                new() { Rev = "f-r2", Length = 9 },
            };
        });

        Assert.Equal(2, (int)result.Properties["FilesWithRevisionsCached"].Value);
        Assert.Equal(3, (int)result.Properties["RevisionsCached"].Value);
    }

    private static PSObject RunBuild(bool includeRevisions, Action<FakeDropboxServiceClient> configure)
    {
        var fake = new FakeDropboxServiceClient(SampleTree());
        configure(fake);
        var dllPath = Path.Combine(AppContext.BaseDirectory, "DbxProvider.dll");
        var cacheDir = Path.Combine(Path.GetTempPath(), "DbxProviderTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(cacheDir);

        try
        {
            using var ps = PowerShell.Create();
            ps.Runspace.SessionStateProxy.SetVariable("fake", fake);
            ps.Runspace.SessionStateProxy.SetVariable("dllPath", dllPath);
            ps.Runspace.SessionStateProxy.SetVariable("cacheDir", cacheDir);
            ps.Runspace.SessionStateProxy.SetVariable("includeRevisions", includeRevisions);
            ps.AddScript(Script);
            var results = ps.Invoke();

            if (results.Count == 0)
            {
                var sb = new StringBuilder("No result returned. PowerShell errors:\n");
                foreach (var e in ps.Streams.Error) sb.AppendLine(e.ToString());
                Assert.Fail(sb.ToString());
            }

            return results[0];
        }
        finally
        {
            try { Directory.Delete(cacheDir, recursive: true); } catch { /* best effort */ }
        }
    }
}