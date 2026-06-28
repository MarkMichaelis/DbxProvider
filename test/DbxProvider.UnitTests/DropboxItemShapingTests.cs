using DbxProvider.Provider;
using IntelliTect.Dropbox;
using Xunit;

namespace DbxProvider.UnitTests;

/// <summary>
/// Contract tests for the shared <see cref="DropboxItemShaping"/> helper that both
/// the provider and the cmdlet base delegate to, so their pipeline output cannot
/// drift apart (issue #89).
/// </summary>
public class DropboxItemShapingTests
{
    [Fact]
    public void ToDriveQualifiedPSObject_ShadowsPath_AndPreservesRawApiPath()
    {
        var item = new DropboxItem { Name = "file.txt", Path = "/Folder/file.txt", IsFolder = false };

        var pso = DropboxItemShaping.ToDriveQualifiedPSObject(item, "Dbx");

        Assert.Equal(@"Dbx:\Folder\file.txt", pso.Properties["Path"]!.Value);
        Assert.Equal("/Folder/file.txt", pso.Properties["DropboxPath"]!.Value);
    }

    [Fact]
    public void ToDriveQualifiedPSObject_HonorsNonDefaultDriveName()
    {
        var item = new DropboxItem { Name = "b", Path = "/A/b", IsFolder = true };

        var pso = DropboxItemShaping.ToDriveQualifiedPSObject(item, "Work");

        Assert.Equal(@"Work:\A\b", pso.Properties["Path"]!.Value);
    }

    [Theory]
    [InlineData("/A/b.txt", "Dbx", @"Dbx:\A\b.txt")]
    [InlineData("", "Dbx", "Dbx:")]
    [InlineData("/", "Dbx", @"Dbx:\")]
    public void ToDriveQualifiedPath_ConvertsApiPathToDriveQualifiedForm(
        string apiPath, string driveName, string expected)
    {
        Assert.Equal(expected, DropboxItemShaping.ToDriveQualifiedPath(apiPath, driveName));
    }
}
