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
        IReadOnlyList<ErrorRecord> Errors);

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

        if (failOnError && errors.Length > 0)
        {
            var sb = new StringBuilder("PowerShell errors:\n");
            foreach (var e in errors) sb.AppendLine(e.ToString());
            Assert.Fail(sb.ToString());
        }
        return new Result(fake, output, errors);
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
        var text = Encoding.UTF8.GetString(upload.Content);
        Assert.StartsWith("hello", text);
        Assert.Contains("world", text);
    }

    [Fact]
    public void CopyDropboxItemBatch_SurfacesPerEntryFailures_AsErrors()
    {
        var fake = new FakeDropboxServiceClient(SeedItems())
        {
            NextRelocationResult = new DropboxBatchRelocationResult(
                new List<DropboxItem> { new() { Name = "a", Path = "/C/a", IsFolder = false, Id = "id:Ca" } },
                new List<DropboxBatchRelocationError> { new("to/path/conflict") }),
        };

        var result = RunScript(fake,
            "Copy-DropboxItemBatch -FromPath '/A/a','/A/b' -ToPath '/C/a','/C/b' -Confirm:$false",
            failOnError: false);

        Assert.Single(result.Output);
        var error = Assert.Single(result.Errors);
        Assert.Contains("to/path/conflict", error.ToString());
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
    public void InvokeDropboxUpload_RejectsRemovedUpdateWriteMode()
    {
        var fake = new FakeDropboxServiceClient(SeedItems());
        var temp = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".txt");
        File.WriteAllText(temp, "payload");
        try
        {
            var result = RunScript(fake,
                $"Invoke-DropboxUpload -Source '{temp}' -DropboxPath '/A/up.txt' -WriteMode update -Confirm:$false",
                failOnError: false);
            Assert.NotEmpty(result.Errors);
            Assert.Empty(fake.Uploads);
        }
        finally { File.Delete(temp); }
    }
}
