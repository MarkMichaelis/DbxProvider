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
/// Functional tests that run the real <c>Find-DropboxConflict</c> cmdlet
/// in-process against a Dropbox drive backed by the in-memory fake, proving the
/// cmdlet reads conflicts straight from the metadata cache (zero recursive
/// enumeration), auto-refreshes the cache via the account delta cursor first,
/// surfaces a rebuild warning when the cursor is rejected, and archives a legacy
/// state sidecar instead of erroring.
/// </summary>
public class ConflictCmdletHostTests : IDisposable
{
    private readonly string _cacheDir =
        Path.Combine(Path.GetTempPath(), "DbxConflictHostTests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { if (Directory.Exists(_cacheDir)) Directory.Delete(_cacheDir, recursive: true); }
        catch { /* best effort */ }
    }

    private const string SetupAndBuild = @"
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
Build-DropboxCache -Path '/' | Out-Null
";

    private PowerShell NewHost(FakeDropboxServiceClient fake, string statePath = "")
    {
        var ps = PowerShell.Create();
        ps.Runspace.SessionStateProxy.SetVariable("fake", fake);
        ps.Runspace.SessionStateProxy.SetVariable("dllPath",
            Path.Combine(AppContext.BaseDirectory, "DbxProvider.dll"));
        ps.Runspace.SessionStateProxy.SetVariable("cacheDir", _cacheDir);
        ps.Runspace.SessionStateProxy.SetVariable("statePath", statePath);
        return ps;
    }

    private static string Errors(PowerShell ps)
    {
        var sb = new StringBuilder();
        foreach (var e in ps.Streams.Error) sb.AppendLine(e.ToString());
        return sb.ToString();
    }

    [Fact]
    public void FindDropboxConflict_ReadsCache_EmitsZeroByteMatch_WithoutFullEnumeration()
    {
        var fake = new FakeDropboxServiceClient(new List<DropboxItem>
        {
            new() { Name = "A", Path = "/A", IsFolder = true },
            new() { Name = "ok.txt", Path = "/A/ok.txt", IsFolder = false, Length = 10 },
            new() { Name = "report's conflicted copy.docx", Path = "/A/report's conflicted copy.docx", IsFolder = false, Length = 0 },
        });

        using var ps = NewHost(fake);
        ps.AddScript(SetupAndBuild + @"
$matches = @(Find-DropboxConflict)
[pscustomobject]@{ Count = $matches.Count; FirstPath = ($matches | Select-Object -First 1).Path }
");
        var results = ps.Invoke();

        Assert.False(ps.HadErrors, Errors(ps));
        var r = results.Single();
        Assert.Equal(1, (int)r.Properties["Count"].Value);
        Assert.Equal("/A/report's conflicted copy.docx", (string)r.Properties["FirstPath"].Value);

        // The build did exactly one recursive listing; the find added none -- it
        // read the cache, never re-enumerating the tree through the API.
        Assert.Equal(1, fake.FullListCalls);
    }

    [Fact]
    public void FindDropboxConflict_AutoRefresh_PicksUpDeltaAddedConflict_AndReportsCounts()
    {
        var fake = new FakeDropboxServiceClient(new List<DropboxItem>
        {
            new() { Name = "A", Path = "/A", IsFolder = true },
            new() { Name = "normal.txt", Path = "/A/normal.txt", IsFolder = false, Length = 10 },
        });
        // The conflict exists ONLY as a pending account delta, so it can be found
        // only if the cmdlet drains the sync cursor before reading the cache.
        var delta = new DropboxServiceClient.ListFolderDelta { NewCursor = "sync::2", HasMore = false };
        delta.AddsOrUpdates.Add(new DropboxItem
        {
            Name = "late's conflicted copy.txt",
            Path = "/A/late's conflicted copy.txt",
            IsFolder = false,
            Length = 0,
        });
        fake.EnqueueSyncDelta(delta);

        using var ps = NewHost(fake);
        ps.AddScript(SetupAndBuild + @"
$matches = @(Find-DropboxConflict)
[pscustomobject]@{ Count = $matches.Count; FirstPath = ($matches | Select-Object -First 1).Path }
");
        var results = ps.Invoke();

        Assert.False(ps.HadErrors, Errors(ps));
        var r = results.Single();
        Assert.Equal(1, (int)r.Properties["Count"].Value);
        Assert.Equal("/A/late's conflicted copy.txt", (string)r.Properties["FirstPath"].Value);
        Assert.True(fake.ContinueCalls >= 1, "auto-refresh should drain at least one delta page");

        var verbose = string.Join("\n", ps.Streams.Verbose.Select(v => v.Message));
        Assert.Contains("Refreshed cache:", verbose);
        Assert.Contains("1 added", verbose);
    }

    [Fact]
    public void FindDropboxConflict_AutoRefresh_ResetRequired_WarnsToRebuild()
    {
        var fake = new FakeDropboxServiceClient(new List<DropboxItem>
        {
            new() { Name = "A", Path = "/A", IsFolder = true },
            new() { Name = "x's conflicted copy.txt", Path = "/A/x's conflicted copy.txt", IsFolder = false, Length = 0 },
        });
        fake.EnqueueSyncDelta(new DropboxServiceClient.ListFolderDelta { ResetRequired = true });

        using var ps = NewHost(fake);
        ps.AddScript(SetupAndBuild + "Find-DropboxConflict | Out-Null");
        ps.Invoke();

        Assert.False(ps.HadErrors, Errors(ps));
        var warnings = string.Join("\n", ps.Streams.Warning.Select(w => w.Message));
        Assert.Contains("Build-DropboxCacheAll.ps1 -Rebuild", warnings);
    }

    [Fact]
    public void FindDropboxConflict_LegacyStateSidecar_IsArchivedToBak_NoError()
    {
        var fake = new FakeDropboxServiceClient(new List<DropboxItem>
        {
            new() { Name = "A", Path = "/A", IsFolder = true },
            new() { Name = "c's conflicted copy.txt", Path = "/A/c's conflicted copy.txt", IsFolder = false, Length = 0 },
        });

        var statePath = Path.Combine(_cacheDir, "legacy.state.json");
        Directory.CreateDirectory(_cacheDir);
        const string legacyJson = @"{
  ""AccountId"": ""fake-account"",
  ""StartPath"": """",
  ""Pattern"": ""*'s conflicted copy*"",
  ""IncludeNonZero"": false,
  ""Cursor"": ""old-cursor-xyz"",
  ""Matches"": { ""/a/legacy's conflicted copy.txt"": { ""Path"": ""/A/legacy's conflicted copy.txt"", ""Bytes"": 0 } }
}";
        File.WriteAllText(statePath, legacyJson);

        using var ps = NewHost(fake, statePath);
        ps.AddScript(SetupAndBuild + @"
$matches = @(Find-DropboxConflict -StatePath $statePath)
[pscustomobject]@{ Count = $matches.Count }
");
        var results = ps.Invoke();

        Assert.False(ps.HadErrors, Errors(ps));
        Assert.Equal(1, (int)results.Single().Properties["Count"].Value);

        // The obsolete sidecar is archived (data preserved), not left in place.
        Assert.False(File.Exists(statePath), "legacy sidecar should be moved aside");
        Assert.True(File.Exists(statePath + ".bak"), "legacy sidecar should be archived to .bak");
        Assert.Contains("legacy", File.ReadAllText(statePath + ".bak"));
    }

    [Fact]
    public void FindDropboxConflict_NonLegacyStateFile_IsLeftUntouched()
    {
        var fake = new FakeDropboxServiceClient(new List<DropboxItem>
        {
            new() { Name = "A", Path = "/A", IsFolder = true },
            new() { Name = "d's conflicted copy.txt", Path = "/A/d's conflicted copy.txt", IsFolder = false, Length = 0 },
        });

        // A JSON file that is NOT a legacy sidecar (no AccountId/Cursor). It must
        // be left exactly where it is -- never mistaken for legacy state and
        // archived just because it happens to be valid JSON.
        var statePath = Path.Combine(_cacheDir, "not-a-sidecar.json");
        Directory.CreateDirectory(_cacheDir);
        const string foreignJson = @"{ ""note"": ""this is not a conflict-scan sidecar"" }";
        File.WriteAllText(statePath, foreignJson);

        using var ps = NewHost(fake, statePath);
        ps.AddScript(SetupAndBuild + @"
$matches = @(Find-DropboxConflict -StatePath $statePath)
[pscustomobject]@{ Count = $matches.Count }
");
        var results = ps.Invoke();

        Assert.False(ps.HadErrors, Errors(ps));
        // The conflict in the cache is still found -- the cmdlet proceeds normally.
        Assert.Equal(1, (int)results.Single().Properties["Count"].Value);

        // The foreign file is untouched: still present, unchanged, and no .bak.
        Assert.True(File.Exists(statePath), "non-legacy file must be left in place");
        Assert.Equal(foreignJson, File.ReadAllText(statePath));
        Assert.False(File.Exists(statePath + ".bak"), "non-legacy file must not be archived");
    }
}