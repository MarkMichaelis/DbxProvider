using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Management.Automation;
using IntelliTect.Dropbox;
using Xunit;

namespace DbxProvider.ProviderHostTests;

/// <summary>
/// Behavior-first tests proving that a literal <c>[bracket]</c> folder path is
/// enumerated normally (via list_folder) rather than misrouted to search_v2 with
/// its hard 1000-item cap (issue #90). PowerShell treats <c>[</c> as a wildcard
/// metacharacter, but <c>WildcardPattern.ContainsWildcardCharacters</c> must not
/// drive Dropbox enumeration routing for a literal-bracket folder the user
/// addresses with <c>-LiteralPath</c>.
/// </summary>
public class ProviderBracketPathHostTests
{
    // A folder whose name contains literal square brackets, plus a child file.
    private static List<DropboxItem> Tree() => new()
    {
        new() { Name = "Data", Path = "/Data", IsFolder = true, Id = "id:Data" },
        new() { Name = "[archive]", Path = "/Data/[archive]", IsFolder = true, Id = "id:arch" },
        new() { Name = "keep.txt", Path = "/Data/[archive]/keep.txt", IsFolder = false, Id = "id:keep", Length = 5 },
    };

    private static (System.Collections.ObjectModel.Collection<PSObject> Output, IReadOnlyList<ErrorRecord> Errors) RunScript(
        FakeDropboxServiceClient fake, string command)
    {
        var dllPath = Path.Combine(AppContext.BaseDirectory, "DbxProvider.dll");
        var script = string.Join(Environment.NewLine, new[]
        {
            "Import-Module $dllPath",
            "$prov = Get-PSProvider Dropbox",
            "$psdrive = [System.Management.Automation.PSDriveInfo]::new('Dbx', $prov, '\\', 'fake', $null)",
            "$dbx = [DbxProvider.Provider.DropboxDriveInfo]::new($psdrive, $fake)",
            "$ExecutionContext.SessionState.Drive.New($dbx, 'global') | Out-Null",
            command,
        });

        using var ps = PowerShell.Create();
        ps.Runspace.SessionStateProxy.SetVariable("fake", fake);
        ps.Runspace.SessionStateProxy.SetVariable("dllPath", dllPath);
        ps.AddScript(script);
        var output = ps.Invoke();
        return (output, ps.Streams.Error.ToArray());
    }

    [Fact]
    public void GetChildItem_Recurse_LiteralBracketFolder_ListsChildren_NotSearch()
    {
        var fake = new FakeDropboxServiceClient(Tree());

        var (output, errors) = RunScript(fake,
            @"Get-ChildItem -LiteralPath 'Dbx:\Data\[archive]' -Recurse");

        // Misrouting to search_v2 would invoke SearchByFilenameAsync on the (null)
        // underlying client and fault; correct routing lists the folder's children.
        Assert.True(errors.Count == 0,
            string.Join(Environment.NewLine, errors.Select(e => e.ToString())));

        var paths = output.Select(o => (string)o.Properties["DropboxPath"]!.Value).ToList();
        Assert.Contains("/Data/[archive]/keep.txt", paths);
    }
}
