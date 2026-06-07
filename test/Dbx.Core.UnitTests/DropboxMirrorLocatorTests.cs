using System.IO;
using IntelliTect.Dropbox;
using FluentAssertions;
using Xunit;

namespace Dbx.Core.UnitTests;

/// <summary>
/// Verifies that <see cref="DropboxMirrorLocator"/> recovers the local Dropbox
/// folder from the desktop client's <c>info.json</c>, preferring the personal
/// account and tolerating a missing or malformed file by returning
/// <see langword="null"/> (so the caller transparently falls back to the API).
/// </summary>
public class DropboxMirrorLocatorTests
{
    [Fact]
    public void ReadRootFromInfoJson_PersonalAccount_ReturnsItsPath()
    {
        string dir = CreateTempDir();
        string infoPath = Path.Combine(dir, "info.json");
        File.WriteAllText(infoPath,
            "{\"personal\":{\"path\":\"C:\\\\Users\\\\Mark\\\\Dropbox\",\"host\":1}}");

        DropboxMirrorLocator.ReadRootFromInfoJson(infoPath)
            .Should().Be("C:\\Users\\Mark\\Dropbox");
    }

    [Fact]
    public void ReadRootFromInfoJson_PrefersPersonalOverBusiness()
    {
        string dir = CreateTempDir();
        string infoPath = Path.Combine(dir, "info.json");
        File.WriteAllText(infoPath,
            "{\"business\":{\"path\":\"C:\\\\Biz\"},\"personal\":{\"path\":\"C:\\\\Me\"}}");

        DropboxMirrorLocator.ReadRootFromInfoJson(infoPath).Should().Be("C:\\Me");
    }

    [Fact]
    public void ReadRootFromInfoJson_OnlyBusinessAccount_ReturnsBusinessPath()
    {
        string dir = CreateTempDir();
        string infoPath = Path.Combine(dir, "info.json");
        File.WriteAllText(infoPath, "{\"business\":{\"path\":\"C:\\\\Biz\"}}");

        DropboxMirrorLocator.ReadRootFromInfoJson(infoPath).Should().Be("C:\\Biz");
    }

    [Fact]
    public void ReadRootFromInfoJson_MissingFile_ReturnsNull()
    {
        string dir = CreateTempDir();
        string infoPath = Path.Combine(dir, "does-not-exist.json");

        DropboxMirrorLocator.ReadRootFromInfoJson(infoPath).Should().BeNull();
    }

    [Fact]
    public void ReadRootFromInfoJson_MalformedJson_ReturnsNull()
    {
        string dir = CreateTempDir();
        string infoPath = Path.Combine(dir, "info.json");
        File.WriteAllText(infoPath, "not json at all");

        DropboxMirrorLocator.ReadRootFromInfoJson(infoPath).Should().BeNull();
    }

    private static string CreateTempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "dbx-locator-" + Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        return dir;
    }
}
