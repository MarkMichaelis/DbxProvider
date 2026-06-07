using System;
using System.IO;
using System.Text;
using IntelliTect.Dropbox;
using FluentAssertions;
using Xunit;

namespace Dbx.Core.UnitTests;

/// <summary>
/// Verifies <see cref="LocalMirrorResolver"/> only serves a local file in place
/// of an API download when it provably matches the Dropbox master: correct path
/// mapping, size and <c>content_hash</c> gating (so NAS-only or stale files are
/// rejected), and cloud-placeholder detection (so on-demand files are never
/// hydrated). A rejection returns <see langword="null"/> for transparent
/// fallback.
/// </summary>
public class LocalMirrorResolverTests
{
    [Fact]
    public void MapToLocalPath_NestedDropboxPath_JoinsUnderRoot()
    {
        var resolver = new LocalMirrorResolver(new LocalMirrorOptions { Root = "C:\\Mirror" });

        resolver.MapToLocalPath("/Foo/Bar.txt")
            .Should().Be("C:\\Mirror" + Path.DirectorySeparatorChar + "Foo" +
                         Path.DirectorySeparatorChar + "Bar.txt");
    }

    [Fact]
    public void TryOpenVerified_Disabled_ReturnsNull()
    {
        var resolver = new LocalMirrorResolver(
            new LocalMirrorOptions { Enabled = false, Root = "C:\\Mirror" });

        resolver.TryOpenVerified("/a.txt", new DropboxItem { Length = 1 })
            .Should().BeNull();
    }

    [Fact]
    public void TryOpenVerified_FileMissing_ReturnsNull()
    {
        string root = CreateTempDir();
        var resolver = new LocalMirrorResolver(new LocalMirrorOptions { Root = root });

        resolver.TryOpenVerified("/missing.txt", new DropboxItem { Length = 1 })
            .Should().BeNull();
    }

    [Fact]
    public void TryOpenVerified_ContentHashMatches_ReturnsLocalBytes()
    {
        string root = CreateTempDir();
        byte[] bytes = Encoding.ASCII.GetBytes("hello world");
        File.WriteAllBytes(Path.Combine(root, "a.txt"), bytes);
        var master = new DropboxItem
        {
            Length = (ulong)bytes.Length,
            ContentHash = DropboxContentHasher.ComputeHash(new MemoryStream(bytes)),
        };
        var resolver = new LocalMirrorResolver(new LocalMirrorOptions { Root = root });

        using Stream? stream = resolver.TryOpenVerified("/a.txt", master);

        stream.Should().NotBeNull();
        using var reader = new StreamReader(stream!);
        reader.ReadToEnd().Should().Be("hello world");
    }

    [Fact]
    public void TryOpenVerified_ContentHashDiffers_ReturnsNull()
    {
        string root = CreateTempDir();
        File.WriteAllText(Path.Combine(root, "a.txt"), "local copy is different");
        var master = new DropboxItem
        {
            Length = (ulong)"local copy is different".Length,
            ContentHash = new string('0', 64),
        };
        var resolver = new LocalMirrorResolver(new LocalMirrorOptions { Root = root });

        resolver.TryOpenVerified("/a.txt", master).Should().BeNull();
    }

    [Fact]
    public void TryOpenVerified_SizeDiffers_ReturnsNullWithoutHashing()
    {
        string root = CreateTempDir();
        File.WriteAllText(Path.Combine(root, "a.txt"), "short");
        var master = new DropboxItem { Length = 999_999, ContentHash = new string('a', 64) };
        var resolver = new LocalMirrorResolver(new LocalMirrorOptions { Root = root });

        resolver.TryOpenVerified("/a.txt", master).Should().BeNull();
    }

    [Fact]
    public void TryOpenVerified_VerifyDisabledAndSizeMatches_ServesLocal()
    {
        string root = CreateTempDir();
        File.WriteAllText(Path.Combine(root, "a.txt"), "abcde");
        var master = new DropboxItem { Length = 5, ContentHash = string.Empty };
        var resolver = new LocalMirrorResolver(
            new LocalMirrorOptions { Root = root, VerifyContentHash = false });

        using Stream? stream = resolver.TryOpenVerified("/a.txt", master);

        stream.Should().NotBeNull();
    }

    [Fact]
    public void TryOpenVerified_VerifyEnabledButMasterHashMissing_ReturnsNull()
    {
        string root = CreateTempDir();
        File.WriteAllText(Path.Combine(root, "a.txt"), "abcde");
        var master = new DropboxItem { Length = 5, ContentHash = string.Empty };
        var resolver = new LocalMirrorResolver(new LocalMirrorOptions { Root = root });

        resolver.TryOpenVerified("/a.txt", master).Should().BeNull();
    }

    [Theory]
    [InlineData(0x00400000)] // FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS (Dropbox online-only)
    [InlineData(0x00040000)] // FILE_ATTRIBUTE_RECALL_ON_OPEN
    [InlineData(0x00001000)] // FILE_ATTRIBUTE_OFFLINE
    public void IsCloudPlaceholder_RecallOrOfflineAttributes_AreTreatedAsPlaceholders(int attr)
    {
        LocalMirrorResolver.IsCloudPlaceholder((FileAttributes)attr).Should().BeTrue();
    }

    [Fact]
    public void IsCloudPlaceholder_NormalFile_IsNotPlaceholder()
    {
        LocalMirrorResolver.IsCloudPlaceholder(FileAttributes.Normal).Should().BeFalse();
    }

    private static string CreateTempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "dbx-mirror-" + Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        return dir;
    }
}
