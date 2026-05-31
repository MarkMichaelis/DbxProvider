using DbxProvider.Models;
using Xunit;

namespace DbxProvider.UnitTests;

/// <summary>
/// Verifies the FileSystem-parity computed properties on <see cref="DropboxItem"/>:
/// FullName, LastWriteTime, Extension, BaseName. See docs/FILESYSTEM-PARITY.md.
/// </summary>
public class DropboxItemFileSystemParityTests
{
    [Fact]
    public void FullName_IsAliasForPath()
    {
        var item = new DropboxItem { Name = "f.txt", Path = "/a/b/f.txt" };
        Assert.Equal("/a/b/f.txt", item.FullName);
    }

    [Fact]
    public void LastWriteTime_IsAliasForServerModified()
    {
        var when = new System.DateTime(2025, 1, 2, 3, 4, 5, System.DateTimeKind.Utc);
        var item = new DropboxItem { Name = "f.txt", ServerModified = when };
        Assert.Equal(when, item.LastWriteTime);
    }

    [Theory]
    [InlineData("file.txt", ".txt", "file")]
    [InlineData("archive.tar.gz", ".gz", "archive.tar")]
    [InlineData("README", "", "README")]
    [InlineData(".gitignore", "", ".gitignore")] // leading dot is not an extension
    [InlineData("", "", "")]
    public void Extension_And_BaseName_MatchFileSystemSemantics(string name, string ext, string baseName)
    {
        var item = new DropboxItem { Name = name, IsFolder = false };
        Assert.Equal(ext, item.Extension);
        Assert.Equal(baseName, item.BaseName);
    }

    [Fact]
    public void Folder_HasEmptyExtension_AndBaseNameEqualsName()
    {
        var item = new DropboxItem { Name = "my.folder", IsFolder = true };
        Assert.Equal(string.Empty, item.Extension);
        Assert.Equal("my.folder", item.BaseName);
    }
}
