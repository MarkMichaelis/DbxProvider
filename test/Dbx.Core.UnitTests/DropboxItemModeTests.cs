using IntelliTect.Dropbox;
using FluentAssertions;
using Xunit;

namespace Dbx.Core.UnitTests;

/// <summary>
/// Behavior tests for <see cref="DropboxItem.Mode"/> -- the fixed-position status
/// flag mask shown in the provider's table listing. Each position maps to a real
/// <see cref="DropboxItem"/> signal (folder, shared, symlink, cloud-only,
/// zero-byte, conflicted copy), so a regression in the mask fails here for a
/// behavioral reason rather than only altering display output.
/// </summary>
public class DropboxItemModeTests
{
    [Fact]
    public void Mode_PlainFile_IsAllDashes()
    {
        var item = new DropboxItem { Name = "report.txt", Path = "/A/report.txt", IsFolder = false, Length = 100 };

        item.Mode.Should().Be("------");
    }

    [Fact]
    public void Mode_Folder_SetsFolderFlag()
    {
        var item = new DropboxItem { Name = "A", Path = "/A", IsFolder = true };

        item.Mode.Should().Be("d-----");
    }

    [Fact]
    public void Mode_SharedFolder_SetsSharedFlag()
    {
        var item = new DropboxItem { Name = "Team", Path = "/Team", IsFolder = true, SharedFolderId = "sf:1" };

        item.Mode.Should().Be("ds----");
    }

    [Fact]
    public void Mode_FileUnderSharedFolder_SetsSharedFlag()
    {
        var item = new DropboxItem { Name = "f.txt", Path = "/Team/f.txt", ParentSharedFolderId = "sf:1", Length = 5 };

        item.Mode.Should().Be("-s----");
    }

    [Fact]
    public void Mode_ExplicitSharedMembers_SetsSharedFlag()
    {
        var item = new DropboxItem { Name = "f.txt", Path = "/f.txt", HasExplicitSharedMembers = true, Length = 5 };

        item.Mode.Should().Be("-s----");
    }

    [Fact]
    public void Mode_Symlink_SetsSymlinkFlag()
    {
        var item = new DropboxItem { Name = "link", Path = "/link", SymlinkTarget = "/target", Length = 0 };

        item.Mode.Should().Be("--l-z-");
    }

    [Fact]
    public void Mode_CloudOnlyFile_SetsCloudFlag()
    {
        var item = new DropboxItem { Name = "doc.paper", Path = "/doc.paper", IsDownloadable = false, Length = 10 };

        item.Mode.Should().Be("---c--");
    }

    [Fact]
    public void Mode_ZeroByteFile_SetsZeroFlag()
    {
        var item = new DropboxItem { Name = "empty.txt", Path = "/empty.txt", IsFolder = false, Length = 0 };

        item.Mode.Should().Be("----z-");
    }

    [Fact]
    public void Mode_EmptyFolder_DoesNotSetZeroFlag()
    {
        var item = new DropboxItem { Name = "A", Path = "/A", IsFolder = true, Length = 0 };

        item.Mode.Should().Be("d-----");
    }

    [Theory]
    [InlineData("report's conflicted copy.docx")]
    [InlineData("data (Mark's conflicted copy 2024-01-01).txt")]
    [InlineData("UPPER CONFLICTED COPY.txt")]
    public void Mode_ZeroByteConflictedCopy_SetsZeroAndConflictFlags(string name)
    {
        var item = new DropboxItem { Name = name, Path = "/A/" + name, IsFolder = false, Length = 0 };

        item.Mode.Should().Be("----zx");
    }

    [Fact]
    public void Mode_NonZeroConflictedCopy_SetsConflictFlagWithoutZero()
    {
        var item = new DropboxItem
        {
            Name = "big's conflicted copy.bin",
            Path = "/big's conflicted copy.bin",
            IsFolder = false,
            Length = 1024,
        };

        item.Mode.Should().Be("-----x");
    }

    [Fact]
    public void IsConflictedCopy_FolderNamedLikeConflict_IsFalse()
    {
        var item = new DropboxItem { Name = "conflicted copy", Path = "/conflicted copy", IsFolder = true };

        item.IsConflictedCopy.Should().BeFalse();
        item.Mode.Should().Be("d-----");
    }
}
