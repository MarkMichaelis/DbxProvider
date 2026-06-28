using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Management.Automation;
using System.Text;
using IntelliTect.Dropbox;
using Xunit;

namespace DbxProvider.ProviderHostTests;

/// <summary>
/// Host (in-process PowerShell) behavior tests covering the multi-model review
/// fixes: provider items carry drive-qualified paths so they route back through
/// the provider; raw byte writes are not corrupted; Add-Content appends instead of
/// overwriting; batch copy/move surface per-entry failures and honor ShouldProcess;
/// and Invoke-DropboxUpload honors ShouldProcess and write-mode selection.
/// </summary>
public class ProviderReviewFixTests
{
    private static List<DropboxItem> SeedItems() => new()
    {
        new() { Name = "A", Path = "/A", IsFolder = true, Id = "id:A" },
        new() { Name = "b.txt", Path = "/A/b.txt", IsFolder = false, Id = "id:b", Length = 12 },
    };

    private sealed record Result(
        FakeDropboxServiceClient Fake,
        Collection<PSObject> Output,
        IReadOnlyList<ErrorRecord> Errors,
        IReadOnlyList<string> Warnings);

    private static Result RunScript(FakeDropboxServiceClient fake, string command, bool failOnError = true)
    {
        var dllPath = Path.Combine(AppContext.BaseDirectory, "DbxProvider.dll");
        var script = string.Join("\n", new[]
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
        var errors = ps.Streams.Error.ToArray();
        var warnings = ps.Streams.Warning.Select(w => w.Message).ToArray();

        if (failOnError && errors.Length > 0)
        {
            var sb = new StringBuilder("PowerShell errors:\n");
            foreach (var e in errors) sb.AppendLine(e.ToString());
            Assert.Fail(sb.ToString());
        }
        return new Result(fake, output, errors, warnings);
    }

    [Fact]
    public void GetChildItem_EmitsDriveQualifiedPath_WithRawApiPathPreserved()
    {
        var result = RunScript(new FakeDropboxServiceClient(SeedItems()),
            "Get-ChildItem 'Dbx:\\A'");

        var item = Assert.Single(result.Output);
        Assert.Equal("Dbx:\\A\\b.txt", item.Properties["Path"]!.Value);
        Assert.Equal("/A/b.txt", item.Properties["DropboxPath"]!.Value);
    }

    [Fact]
    public void GetChildItem_PipedToRemoveItem_RoutesDeleteThroughProvider()
    {
        var fake = new FakeDropboxServiceClient(SeedItems());

        RunScript(fake, "Get-ChildItem 'Dbx:\\A' | Remove-Item -Confirm:$false");

        Assert.Contains("/A/b.txt", fake.Deletes);
    }

    [Fact]
    public void SetContent_AsByteStream_WritesRawBytes_NotText()
    {
        var fake = new FakeDropboxServiceClient(SeedItems());

        RunScript(fake, "Set-Content -LiteralPath 'Dbx:\\A\\b.txt' -AsByteStream -Value ([byte[]](65,66))");

        var upload = Assert.Single(fake.Uploads);
        Assert.Equal(new byte[] { 65, 66 }, upload.Content);
    }

    [Fact]
    public void AddContent_AppendsToExistingContent_InsteadOfOverwriting()
    {
        var fake = new FakeDropboxServiceClient(SeedItems());
        fake.FileBytes["/A/b.txt"] = Encoding.UTF8.GetBytes("hello");

        RunScript(fake, "Add-Content -LiteralPath 'Dbx:\\A\\b.txt' -Value 'world'");

        var upload = fake.Uploads.Last();
        // Byte-level: existing content must be preserved verbatim with NO UTF-8 BOM
        // injected before it, then the appended line follows.
        Assert.False(StartsWithUtf8Bom(upload.Content), "append must not inject a UTF-8 BOM before existing content");
        var text = Encoding.UTF8.GetString(upload.Content);
        Assert.StartsWith("hello", text);
        Assert.Contains("world", text);
    }

    private static bool StartsWithUtf8Bom(byte[] bytes) =>
        bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;

    [Fact]
    public void AddContent_WhenExistingFileReadFails_DoesNotOverwrite()
    {
        var fake = new FakeDropboxServiceClient(SeedItems())
        {
            // The file exists, but reading it for the append preload fails transiently.
            DownloadException = new IOException("transient network failure"),
        };

        var result = RunScript(fake, "Add-Content -LiteralPath 'Dbx:\\A\\b.txt' -Value 'world'",
            failOnError: false);

        Assert.NotEmpty(result.Errors);
        Assert.Empty(fake.Uploads); // must NOT replace the existing file with just 'world'
    }

    [Fact]
    public void SetContent_AsByteStream_WithStringValue_DoesNotCorruptOrThrow()
    {
        var fake = new FakeDropboxServiceClient(SeedItems());

        var result = RunScript(fake,
            "Set-Content -LiteralPath 'Dbx:\\A\\b.txt' -AsByteStream -Value 'A'",
            failOnError: false);

        Assert.Empty(result.Errors);
        var upload = Assert.Single(fake.Uploads);
        Assert.Equal(new byte[] { 0x41 }, upload.Content);
    }

    [Fact]
    public void CopyDropboxItemBatch_SurfacesPerEntryFailures_AsErrors()
    {
        var fake = new FakeDropboxServiceClient(SeedItems())
        {
            NextRelocationResult = new DropboxBatchRelocationResult(
                new List<DropboxItem> { new() { Name = "a", Path = "/C/a", IsFolder = false, Id = "id:Ca" } },
                new List<DropboxBatchRelocationError> { new("to/path/conflict", "/A/b", "/C/b") }),
        };

        var result = RunScript(fake,
            "Copy-DropboxItemBatch -FromPath '/A/a','/A/b' -ToPath '/C/a','/C/b' -Confirm:$false",
            failOnError: false);

        Assert.Single(result.Output);
        var error = Assert.Single(result.Errors);
        Assert.Contains("to/path/conflict", error.ToString());
        // The failing entry must be identifiable: its source path is the error target.
        Assert.Equal("/A/b", error.TargetObject);
        Assert.Contains("/A/b", error.Exception.Message);
    }

    [Fact]
    public void MoveDropboxItemBatch_WhatIf_DoesNotInvokeService()
    {
        var fake = new FakeDropboxServiceClient(SeedItems())
        {
            NextRelocationResult = new DropboxBatchRelocationResult(
                new List<DropboxItem> { new() { Name = "a", Path = "/C/a", IsFolder = false, Id = "id:Ca" } },
                new List<DropboxBatchRelocationError>()),
        };

        var result = RunScript(fake,
            "Move-DropboxItemBatch -FromPath '/A/a' -ToPath '/C/a' -WhatIf");

        Assert.Empty(result.Output);
    }

    [Fact]
    public void InvokeDropboxUpload_WhatIf_DoesNotUpload()
    {
        var fake = new FakeDropboxServiceClient(SeedItems());
        var temp = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".txt");
        File.WriteAllText(temp, "payload");
        try
        {
            RunScript(fake, $"Invoke-DropboxUpload -Source '{temp}' -DropboxPath '/A/up.txt' -WhatIf");
            Assert.Empty(fake.Uploads);
        }
        finally { File.Delete(temp); }
    }

    [Theory]
    [InlineData("-WriteMode add", "add")]
    [InlineData("-WriteMode overwrite", "overwrite")]
    [InlineData("-WriteMode add -Force", "overwrite")]
    public void InvokeDropboxUpload_SelectsWriteMode(string args, string expectedMode)
    {
        var fake = new FakeDropboxServiceClient(SeedItems());
        var temp = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".txt");
        File.WriteAllText(temp, "payload");
        try
        {
            RunScript(fake, $"Invoke-DropboxUpload -Source '{temp}' -DropboxPath '/A/up.txt' {args} -Confirm:$false");
            var upload = Assert.Single(fake.Uploads);
            Assert.Equal(expectedMode, upload.Mode);
        }
        finally { File.Delete(temp); }
    }

    [Fact]
    public void InvokeDropboxUpload_UpdateWriteMode_WarnsAndTreatsAsOverwrite()
    {
        var fake = new FakeDropboxServiceClient(SeedItems());
        var temp = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".txt");
        File.WriteAllText(temp, "payload");
        try
        {
            // 'update' was previously accepted (and silently overwrote). It stays
            // accepted for back-compat but must now WARN and behave as overwrite.
            var result = RunScript(fake,
                $"Invoke-DropboxUpload -Source '{temp}' -DropboxPath '/A/up.txt' -WriteMode update -Confirm:$false",
                failOnError: false);
            Assert.Empty(result.Errors);
            Assert.Contains(result.Warnings, w => w.Contains("update", StringComparison.OrdinalIgnoreCase));
            var upload = Assert.Single(fake.Uploads);
            Assert.Equal("overwrite", upload.Mode);
        }
        finally { File.Delete(temp); }
    }
}
