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
/// Behavior-first tests proving that an item's <c>PSPath</c> round-trips through
/// the Dropbox provider like the built-in FileSystem provider:
/// <c>Get-ChildItem Dbx:\X | %% { Get-ChildItem $_.PSPath }</c> returns X's
/// children. PowerShell resolves a provider-qualified <c>PSPath</c> with a null
/// <c>PSDriveInfo</c>; before the fix the provider's <c>GetService()</c> threw and
/// the swallowed exception yielded zero items. These host PowerShell in-process
/// against an in-memory fake service (no Dropbox credentials needed). Reverting
/// the <c>ResolveDriveInfo</c> fallback in <c>DropboxProvider</c> makes the
/// PSPath lookups return nothing, failing these assertions.
/// </summary>
public class PSPathRoundTripTests
{
    private const string Script = @"
$ErrorActionPreference = 'Stop'
Import-Module $dllPath
$prov = Get-PSProvider Dropbox
$psdrive = [System.Management.Automation.PSDriveInfo]::new('Dbx', $prov, '\', 'fake', $null)
$dbx = [DbxProvider.Provider.DropboxDriveInfo]::new($psdrive, $fake)
$ExecutionContext.SessionState.Drive.New($dbx, 'global') | Out-Null

# The reported repro: list a folder, then re-list via the emitted PSPath.
$folder = Get-ChildItem Dbx:\ | Where-Object { $_.PSIsContainer } | Select-Object -First 1
$viaLiteralPSPath = @(Get-ChildItem -LiteralPath $folder.PSPath)
$itemViaPSPath    = Get-Item -LiteralPath $folder.PSPath

Push-Location Dbx:\
Set-Location -LiteralPath $folder.PSPath
$locationName = (Get-Location).ProviderPath
$locationProvider = (Get-Location).Provider.Name
Pop-Location

[pscustomobject]@{
    FolderName       = $folder.Name
    PSPath           = [string]$folder.PSPath
    RootChildCount   = (@(Get-ChildItem Dbx:\)).Count
    PSPathChildCount = $viaLiteralPSPath.Count
    PSPathChildName  = ($viaLiteralPSPath | Select-Object -First 1).Name
    GetItemName      = $itemViaPSPath.Name
    LocationName     = $locationName
    LocationProvider = $locationProvider
}
";

    private static PSObject RunScenario()
    {
        var items = new List<DropboxItem>
        {
            new() { Name = "A", Path = "/A", IsFolder = true, Id = "id:A" },
            new() { Name = "b.txt", Path = "/A/b.txt", IsFolder = false, Id = "id:b" },
        };
        var fake = new FakeDropboxServiceClient(items);
        var dllPath = Path.Combine(AppContext.BaseDirectory, "DbxProvider.dll");

        using var ps = PowerShell.Create();
        ps.Runspace.SessionStateProxy.SetVariable("fake", fake);
        ps.Runspace.SessionStateProxy.SetVariable("dllPath", dllPath);
        ps.AddScript(Script);
        var results = ps.Invoke();

        if (results.Count == 0)
        {
            var sb = new StringBuilder("No result returned. PowerShell errors:\n");
            foreach (var e in ps.Streams.Error)
                sb.AppendLine(e.ToString());
            Assert.Fail(sb.ToString());
        }

        return results.Single();
    }

    [Fact]
    public void GetChildItem_ViaPSPath_ReturnsFolderChildren()
    {
        var r = RunScenario();
        Assert.Equal("A", (string)r.Properties["FolderName"].Value);
        Assert.Equal(1, (int)r.Properties["PSPathChildCount"].Value);
        Assert.Equal("b.txt", (string)r.Properties["PSPathChildName"].Value);
    }

    [Fact]
    public void GetItem_ViaPSPath_ReturnsItem()
    {
        var r = RunScenario();
        Assert.Equal("A", (string)r.Properties["GetItemName"].Value);
    }

    [Fact]
    public void SetLocation_ViaPSPath_MovesIntoFolder()
    {
        var r = RunScenario();
        Assert.Equal("Dropbox", (string)r.Properties["LocationProvider"].Value);
        Assert.Equal("A", (string)r.Properties["LocationName"].Value);
    }

    [Fact]
    public void GetChildItem_DriveRoot_StillWorks()
    {
        var r = RunScenario();
        Assert.Equal(1, (int)r.Properties["RootChildCount"].Value);
    }
}
