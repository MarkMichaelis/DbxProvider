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
/// Functional test that runs the real provider <c>Get-ChildItem</c> search
/// auto-route in-process against the in-memory fake. It proves the recursive,
/// filtered listing is exhaustive and no longer silently truncates at the old
/// hard-coded 1000-result cap now that the provider calls the shared exhaustive
/// <see cref="DropboxServiceClient.SearchByFilenameAsync"/> with its default.
/// </summary>
public class ProviderSearchRouteHostTests
{
    private const string Script = @"
$ErrorActionPreference = 'Stop'
Import-Module $dllPath
$prov = Get-PSProvider Dropbox
$psdrive = [System.Management.Automation.PSDriveInfo]::new('Dbx', $prov, '\', 'fake', $null)
$dbx = [DbxProvider.Provider.DropboxDriveInfo]::new($psdrive, $fake)
$ExecutionContext.SessionState.Drive.New($dbx, 'global') | Out-Null

$items = @(Get-ChildItem -Path 'Dbx:\' -Filter '*conflicted*' -Recurse)
[pscustomobject]@{ Count = $items.Count }
";

    [Fact]
    public void GetChildItem_RecursiveFilteredSearch_IsExhaustive_DoesNotTruncateAt1000()
    {
        // Seed more than the old 1000 cap so truncation would be observable.
        const int total = 1001;
        var matches = Enumerable.Range(0, total)
            .Select(i => new DropboxItem
            {
                Name = $"file{i} conflicted copy.txt",
                Path = $"/A/file{i} conflicted copy.txt",
                IsFolder = false,
                Length = 0,
            })
            .ToList();
        var fake = new FakeDropboxServiceClient(new List<DropboxItem>());
        fake.EnqueueSearchPage("", matches);

        var dllPath = Path.Combine(AppContext.BaseDirectory, "DbxProvider.dll");
        using var ps = PowerShell.Create();
        ps.Runspace.SessionStateProxy.SetVariable("fake", fake);
        ps.Runspace.SessionStateProxy.SetVariable("dllPath", dllPath);
        ps.AddScript(Script);
        var results = ps.Invoke();

        if (results.Count == 0)
        {
            var sb = new StringBuilder("No result returned. PowerShell errors:\n");
            foreach (var e in ps.Streams.Error) sb.AppendLine(e.ToString());
            Assert.Fail(sb.ToString());
        }

        var count = (int)results.Single().Properties["Count"].Value;
        Assert.Equal(total, count);
    }
}