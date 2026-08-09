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
/// Behavior-first tests proving the Dropbox provider lets a cooperative pipeline
/// stop propagate. <c>Get-ChildItem Dbx:\ | Select-Object -First N</c> stops the
/// upstream enumeration once it has N items, which makes <c>WriteItemObject</c>
/// throw <see cref="PipelineStoppedException"/>; the provider must let that stop
/// propagate rather than swallowing it into a <c>GetChildItemsFailed</c> /
/// <c>GetChildNamesFailed</c> error. Hosts PowerShell in-process against an
/// in-memory fake service (no Dropbox credentials needed).
/// </summary>
public class GetChildItemsPipelineStopHostTests
{
    private const string Setup = @"
$ErrorActionPreference = 'Stop'
Import-Module $dllPath
$prov = Get-PSProvider Dropbox
$psdrive = [System.Management.Automation.PSDriveInfo]::new('Dbx', $prov, '\', 'fake', $null)
$dbx = [DbxProvider.Provider.DropboxDriveInfo]::new($psdrive, $fake)
$ExecutionContext.SessionState.Drive.New($dbx, 'global') | Out-Null
";

    // Three children at the root so '-First 1' stops the enumeration well before
    // it is exhausted, guaranteeing the provider observes the pipeline stop.
    private static List<DropboxItem> Tree() => new()
    {
        new() { Name = "a.txt", Path = "/a.txt", IsFolder = false, Id = "id:a" },
        new() { Name = "b.txt", Path = "/b.txt", IsFolder = false, Id = "id:b" },
        new() { Name = "c.txt", Path = "/c.txt", IsFolder = false, Id = "id:c" },
    };

    private static PowerShell NewHost()
    {
        var fake = new FakeDropboxServiceClient(Tree());
        var ps = PowerShell.Create();
        ps.Runspace.SessionStateProxy.SetVariable("fake", fake);
        ps.Runspace.SessionStateProxy.SetVariable("dllPath",
            Path.Combine(AppContext.BaseDirectory, "DbxProvider.dll"));
        return ps;
    }

    private static string Errors(PowerShell ps)
    {
        var sb = new StringBuilder();
        foreach (var e in ps.Streams.Error) sb.AppendLine(e.ToString());
        return sb.ToString();
    }

    [Fact]
    public void GetChildItem_PipedToSelectFirst_StopsCleanly_WithoutError()
    {
        using var ps = NewHost();
        ps.AddScript(Setup + "Get-ChildItem Dbx:\\ | Select-Object -First 1");
        var results = ps.Invoke();

        Assert.False(ps.HadErrors, Errors(ps));
        Assert.Single(results);
    }

    [Fact]
    public void GetChildName_PipedToSelectFirst_StopsCleanly_WithoutError()
    {
        using var ps = NewHost();
        // -Name routes through GetChildNames rather than GetChildItems.
        ps.AddScript(Setup + "Get-ChildItem Dbx:\\ -Name | Select-Object -First 1");
        var results = ps.Invoke();

        Assert.False(ps.HadErrors, Errors(ps));
        Assert.Single(results);
    }
}
