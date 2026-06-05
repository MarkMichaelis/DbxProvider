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

$matches = @(Find-DropboxConflict -StatePath $statePath)
[pscustomobject]@{
    Count       = $matches.Count
    FirstPath   = ($matches | Select-Object -First 1).Path
    StateExists = (Test-Path -LiteralPath $statePath)
}
";

    [Fact]
    public void FindDropboxConflict_ColdRun_EmitsZeroByteMatch_AndSavesState()
    {
        var items = new List<DropboxItem>
        {
            new() { Name = "ok.txt", Path = "/A/ok.txt", IsFolder = false, Length = 10 },
            new() { Name = "report's conflicted copy.docx", Path = "/A/report's conflicted copy.docx", IsFolder = false, Length = 0 },
        };
        var fake = new FakeDropboxServiceClient(items);
        var dllPath = Path.Combine(AppContext.BaseDirectory, "DbxProvider.dll");
        var statePath = Path.Combine(Path.GetTempPath(), "DbxProviderTests", Guid.NewGuid().ToString("N") + ".json");

        try
        {
            using var ps = PowerShell.Create();
            ps.Runspace.SessionStateProxy.SetVariable("fake", fake);
            ps.Runspace.SessionStateProxy.SetVariable("dllPath", dllPath);
            ps.Runspace.SessionStateProxy.SetVariable("statePath", statePath);
            ps.AddScript(Script);
            var results = ps.Invoke();

            if (results.Count == 0)
            {
                var sb = new StringBuilder("No result returned. PowerShell errors:\n");
                foreach (var e in ps.Streams.Error) sb.AppendLine(e.ToString());
                Assert.Fail(sb.ToString());
            }

            var r = results.Single();
            Assert.Equal(1, (int)r.Properties["Count"].Value);
            Assert.Equal("/A/report's conflicted copy.docx", (string)r.Properties["FirstPath"].Value);
            Assert.True(Convert.ToBoolean(LanguagePrimitives.ConvertTo(r.Properties["StateExists"].Value, typeof(bool))));
        }
        finally
        {
            try { File.Delete(statePath); } catch { /* best effort */ }
        }
    }
}