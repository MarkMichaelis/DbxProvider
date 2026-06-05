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
/// Behavior-first tests proving that writing a file through the provider produces
/// the minimal number of Dropbox server revisions. PowerShell <c>Set-Content</c>
/// calls <c>ClearContent</c> (an implicit truncate) before <c>GetContentWriter</c>;
/// the provider must NOT upload a separate zero-byte intermediate revision for that
/// implicit clear (a concurrent Dropbox sync client can race it into a zero-byte
/// "conflicted copy"). An explicit <c>Clear-Content</c> must still upload zero bytes.
/// These host PowerShell in-process against the recording fake (no credentials).
/// </summary>
public class ContentWriteRevisionTests
{
    private static FakeDropboxServiceClient RunScript(string command)
    {
        var items = new List<DropboxItem>
        {
            new() { Name = "A", Path = "/A", IsFolder = true, Id = "id:A" },
            new() { Name = "b.txt", Path = "/A/b.txt", IsFolder = false, Id = "id:b", Length = 12 },
        };
        var fake = new FakeDropboxServiceClient(items);
        var dllPath = Path.Combine(AppContext.BaseDirectory, "DbxProvider.dll");

        var script = string.Join("\n", new[]
        {
            "$ErrorActionPreference = 'Stop'",
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
        ps.Invoke();

        if (ps.Streams.Error.Count > 0)
        {
            var sb = new StringBuilder("PowerShell errors:\n");
            foreach (var e in ps.Streams.Error) sb.AppendLine(e.ToString());
            Assert.Fail(sb.ToString());
        }
        return fake;
    }

    [Fact]
    public void SetContent_ProducesSingleOverwriteUpload_NoZeroByteIntermediate()
    {
        var fake = RunScript("Set-Content -LiteralPath 'Dbx:\\A\\b.txt' -Value 'hello world'");

        Assert.Single(fake.Uploads);
        Assert.Equal("/A/b.txt", fake.Uploads[0].Path);
        Assert.True(fake.Uploads[0].Length > 0,
            $"Expected the single upload to carry content, but it was {fake.Uploads[0].Length} bytes.");
    }

    [Fact]
    public void ClearContent_TruncatesToZeroBytes()
    {
        var fake = RunScript("Clear-Content -LiteralPath 'Dbx:\\A\\b.txt'");

        Assert.Single(fake.Uploads);
        Assert.Equal("/A/b.txt", fake.Uploads[0].Path);
        Assert.Equal(0, fake.Uploads[0].Length);
    }

    [Fact]
    public void SetContent_EmptyString_ProducesSingleUpload_NoZeroByteIntermediate()
    {
        // Set-Content -Value '' still calls Write (with an empty line), so it must
        // overwrite the file in exactly one revision -- and crucially must NOT emit a
        // separate zero-byte intermediate from the implicit clear.
        var fake = RunScript("Set-Content -LiteralPath 'Dbx:\\A\\b.txt' -Value ''");

        Assert.Single(fake.Uploads);
        Assert.Equal("/A/b.txt", fake.Uploads[0].Path);
    }

    [Fact]
    public void SetContent_EmptyArray_ProducesSingleUpload_NoZeroByteIntermediate()
    {
        // Set-Content -Value @() yields a writer that receives no content; the single
        // overwrite truncates/replaces the file in one revision (matching the
        // FileSystem provider), with no separate intermediate revision.
        var fake = RunScript("Set-Content -LiteralPath 'Dbx:\\A\\b.txt' -Value @()");

        Assert.Single(fake.Uploads);
        Assert.Equal("/A/b.txt", fake.Uploads[0].Path);
    }
}