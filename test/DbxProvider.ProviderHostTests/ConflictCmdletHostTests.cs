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
/// Functional test that runs the real <c>Find-DropboxConflict</c> cmdlet
/// in-process against a Dropbox drive backed by the in-memory fake, proving the
/// cmdlet wires the scanner, persists state, and emits matches end-to-end.
/// </summary>
public class ConflictCmdletHostTests
{
    private const string Script = @"
$ErrorActionPreference = 'Stop'
Import-Module $dllPath
$prov = Get-PSProvider Dropbox
$psdrive = [System.Management.Automation.PSDriveInfo]::new('Dbx', $prov, '\', 'fake', $null)
$dbx = [DbxProvider.Provider.DropboxDriveInfo]::new($psdrive, $fake)
$ExecutionContext.SessionState.Drive.New($dbx, 'global') | Out-Null

$matches = @(Find-DropboxConflict @extra -StatePath $statePath)
[pscustomobject]@{
    Count       = $matches.Count
    FirstPath   = ($matches | Select-Object -First 1).Path
    StateExists = (Test-Path -LiteralPath $statePath)
}
";

    private static PSObject RunCmdlet(FakeDropboxServiceClient fake, string statePath, System.Collections.Hashtable extra)
    {
        var dllPath = Path.Combine(AppContext.BaseDirectory, "DbxProvider.dll");
        using var ps = PowerShell.Create();
        ps.Runspace.SessionStateProxy.SetVariable("fake", fake);
        ps.Runspace.SessionStateProxy.SetVariable("dllPath", dllPath);
        ps.Runspace.SessionStateProxy.SetVariable("statePath", statePath);
        ps.Runspace.SessionStateProxy.SetVariable("extra", extra);
        ps.AddScript(Script);
        var results = ps.Invoke();

        if (results.Count == 0)
        {
            var sb = new StringBuilder("No result returned. PowerShell errors:\n");
            foreach (var e in ps.Streams.Error) sb.AppendLine(e.ToString());
            Assert.Fail(sb.ToString());
        }
        return results.Single();
    }

    [Fact]
    public void FindDropboxConflict_ColdRun_DefaultsToSearchDiscovery_EmitsZeroByteMatch()
    {
        var items = new List<DropboxItem>
        {
            new() { Name = "ok.txt", Path = "/A/ok.txt", IsFolder = false, Length = 10 },
        };
        var fake = new FakeDropboxServiceClient(items);
        fake.EnqueueSearchPage("", new[]
        {
            new DropboxItem { Name = "report's conflicted copy.docx", Path = "/A/report's conflicted copy.docx", IsFolder = false, Length = 0 },
        });
        var statePath = Path.Combine(Path.GetTempPath(), "DbxProviderTests", Guid.NewGuid().ToString("N") + ".json");

        try
        {
            var r = RunCmdlet(fake, statePath, new System.Collections.Hashtable());

            Assert.Equal(1, (int)r.Properties["Count"].Value);
            Assert.Equal("/A/report's conflicted copy.docx", (string)r.Properties["FirstPath"].Value);
            Assert.True(Convert.ToBoolean(LanguagePrimitives.ConvertTo(r.Properties["StateExists"].Value, typeof(bool))));
            Assert.True(fake.SearchCalls >= 1);    // cold run went through search_v2
            Assert.Equal(0, fake.FullListCalls);   // NOT a recursive walk
        }
        finally
        {
            try { File.Delete(statePath); } catch { /* best effort */ }
        }
    }

    [Fact]
    public void FindDropboxConflict_Full_ForcesRecursiveWalk_NotSearch()
    {
        var items = new List<DropboxItem>
        {
            new() { Name = "ok.txt", Path = "/A/ok.txt", IsFolder = false, Length = 10 },
            new() { Name = "report's conflicted copy.docx", Path = "/A/report's conflicted copy.docx", IsFolder = false, Length = 0 },
        };
        var fake = new FakeDropboxServiceClient(items);
        var statePath = Path.Combine(Path.GetTempPath(), "DbxProviderTests", Guid.NewGuid().ToString("N") + ".json");

        try
        {
            var r = RunCmdlet(fake, statePath, new System.Collections.Hashtable { ["Full"] = true });

            Assert.Equal(1, (int)r.Properties["Count"].Value);
            Assert.Equal("/A/report's conflicted copy.docx", (string)r.Properties["FirstPath"].Value);
            Assert.Equal(1, fake.FullListCalls); // forced recursive enumeration
            Assert.Equal(0, fake.SearchCalls);   // search_v2 was not used
        }
        finally
        {
            try { File.Delete(statePath); } catch { /* best effort */ }
        }
    }
}