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
/// Functional tests for the new <c>Find-DropboxItem</c> cmdlet: name-wildcard
/// matching, subtree scoping, the zero-byte filter, and the cross-cutting cache
/// auto-refresh (drain the delta cursor first; capture a baseline cursor when
/// none exists yet, without draining). All reads come from the metadata cache
/// with no recursive API enumeration.
/// </summary>
public class FindDropboxItemHostTests : IDisposable
{
    private readonly string _cacheDir =
        Path.Combine(Path.GetTempPath(), "DbxFindItemHostTests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { if (Directory.Exists(_cacheDir)) Directory.Delete(_cacheDir, recursive: true); }
        catch { /* best effort */ }
    }

    private const string Setup = @"
$ErrorActionPreference = 'Stop'
$VerbosePreference = 'Continue'
Import-Module $dllPath
$prov = Get-PSProvider Dropbox
$psdrive = [System.Management.Automation.PSDriveInfo]::new('Dbx', $prov, '\', 'fake', $null)
$dbx = [DbxProvider.Provider.DropboxDriveInfo]::new($psdrive, $fake)
$opts = [IntelliTect.Dropbox.CacheOptions]::new()
$opts.RootDirectoryOverride = $cacheDir
$dbx.InitializeCache('fake-account', 'fake@example.com', $opts)
$ExecutionContext.SessionState.Drive.New($dbx, 'global') | Out-Null
";

    private const string SetupAndBuild = Setup + "Build-DropboxCache -Path '/' | Out-Null\n";

    private static List<DropboxItem> Tree() => new()
    {
        new() { Name = "A", Path = "/A", IsFolder = true },
        new() { Name = "alpha.txt", Path = "/A/alpha.txt", IsFolder = false, Length = 10 },
        new() { Name = "beta.log", Path = "/A/beta.log", IsFolder = false, Length = 0 },
        new() { Name = "B", Path = "/A/B", IsFolder = true },
        new() { Name = "alpha.md", Path = "/A/B/alpha.md", IsFolder = false, Length = 0 },
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

    private static string[] Paths(System.Collections.ObjectModel.Collection<PSObject> results) =>
        results.Select(o => (string)o.Properties["Path"].Value).OrderBy(p => p, StringComparer.Ordinal).ToArray();

    [Fact]
    public void FindDropboxItem_ByNameWildcard_ReturnsCachedMatches_WithoutFullEnumeration()
    {
        var fake = new FakeDropboxServiceClient(Tree());
        using var ps = NewHost(fake);
        ps.AddScript(SetupAndBuild + "Find-DropboxItem -Name 'alpha*'");
        var results = ps.Invoke();

        Assert.False(ps.HadErrors, Errors(ps));
        Assert.Equal(new[] { "/A/B/alpha.md", "/A/alpha.txt" }, Paths(results));

        // Build did one recursive listing; the find added none (pure cache read).
        Assert.Equal(1, fake.FullListCalls);
    }

    [Fact]
    public void FindDropboxItem_PathScope_LimitsToSubtree()
    {
        var fake = new FakeDropboxServiceClient(Tree());
        using var ps = NewHost(fake);
        ps.AddScript(SetupAndBuild + "Find-DropboxItem -Name '*' -Path '/A/B'");
        var results = ps.Invoke();

        Assert.False(ps.HadErrors, Errors(ps));
        Assert.Equal(new[] { "/A/B/alpha.md" }, Paths(results));
    }

    [Fact]
    public void FindDropboxItem_ZeroByteOnly_FiltersToEmptyFiles()
    {
        var fake = new FakeDropboxServiceClient(Tree());
        using var ps = NewHost(fake);
        ps.AddScript(SetupAndBuild + "Find-DropboxItem -Name '*' -ZeroByteOnly");
        var results = ps.Invoke();

        Assert.False(ps.HadErrors, Errors(ps));
        // beta.log (0) and alpha.md (0); alpha.txt (10) excluded; folders excluded.
        Assert.Equal(new[] { "/A/B/alpha.md", "/A/beta.log" }, Paths(results));
    }

    [Fact]
    public void FindDropboxItem_NoSyncCursor_CapturesBaseline_WithoutDraining()
    {
        // No Build -> the cache is empty and has never captured a sync cursor.
        var fake = new FakeDropboxServiceClient(Tree());
        using var ps = NewHost(fake);
        ps.AddScript(Setup + "Find-DropboxItem -Name '*' | Out-Null");
        ps.Invoke();

        Assert.False(ps.HadErrors, Errors(ps));
        // Exactly one baseline cursor capture, and no delta drain at all.
        Assert.Equal(1, fake.GetLatestCursorCalls);
        Assert.Equal(0, fake.ContinueCalls);

        var warnings = string.Join("\n", ps.Streams.Warning.Select(w => w.Message));
        Assert.Contains("Build-DropboxCacheAll.ps1", warnings);
    }
}