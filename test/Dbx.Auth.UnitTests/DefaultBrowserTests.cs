using MarkMichaelis.Dropbox.Auth;
using Xunit;

namespace Dbx.Auth.UnitTests;

/// <summary>
/// Tests for <see cref="DefaultBrowser.Map"/>, the pure registry-string-to-browser
/// mapper extracted from <see cref="DefaultBrowser.Detect"/> so it is unit-testable
/// without touching the live Windows registry.
///
/// These tests pin the contract that:
///  - well-known Chromium-family ProgIds resolve to <c>IsChromiumFamily=true</c>
///    and a non-null <c>ExecutablePath</c> when the command-line is parseable;
///  - Firefox is recognised but reported as non-Chromium (Playwright cannot
///    drive a stock Firefox install);
///  - unknown ProgIds and missing/garbled command-lines degrade gracefully to
///    <c>(null, "unknown", false)</c> so the cmdlet can fall back to the manual
///    registration wizard.
/// </summary>
public class DefaultBrowserTests
{
    [Theory]
    [InlineData("MSEdgeHTM",   "Microsoft Edge")]
    [InlineData("ChromeHTML",  "Google Chrome")]
    [InlineData("BraveHTML",   "Brave")]
    [InlineData("VivaldiHTM",  "Vivaldi")]
    [InlineData("OperaStable", "Opera")]
    [InlineData("ArcHTM",      "Arc")]
    public void Map_RecognisesChromiumFamilyProgIds(string progId, string expectedName)
    {
        var raw = "\"C:\\Program Files\\Browser\\browser.exe\" --single-argument %1";
        var result = DefaultBrowser.Map(progId, raw);

        Assert.Equal(expectedName, result.FriendlyName);
        Assert.True(result.IsChromiumFamily, $"{progId} should be Chromium-family");
        Assert.Equal("C:\\Program Files\\Browser\\browser.exe", result.ExecutablePath);
    }

    [Fact]
    public void Map_RecognisesFirefoxButReportsNonChromium()
    {
        var raw = "\"C:\\Program Files\\Mozilla Firefox\\firefox.exe\" -osint -url \"%1\"";
        var result = DefaultBrowser.Map("FirefoxURL", raw);

        Assert.Equal("Firefox", result.FriendlyName);
        Assert.False(result.IsChromiumFamily);
    }

    [Fact]
    public void Map_UnknownProgId_ReturnsUnknownNonChromium()
    {
        var result = DefaultBrowser.Map("WeirdBrowserHTML", "\"C:\\x\\y.exe\" %1");

        Assert.Equal("unknown", result.FriendlyName);
        Assert.False(result.IsChromiumFamily);
        Assert.Null(result.ExecutablePath);
    }

    [Fact]
    public void Map_NullProgId_ReturnsUnknownNonChromium()
    {
        var result = DefaultBrowser.Map(null, null);

        Assert.Equal("unknown", result.FriendlyName);
        Assert.False(result.IsChromiumFamily);
        Assert.Null(result.ExecutablePath);
    }

    [Fact]
    public void Map_KnownProgIdButUnparseableCommand_ReturnsNullExePathStillChromium()
    {
        // Even when we can't extract the exe path, we still report the family —
        // callers degrade by treating null ExecutablePath as "fall back".
        var result = DefaultBrowser.Map("MSEdgeHTM", null);

        Assert.Equal("Microsoft Edge", result.FriendlyName);
        Assert.True(result.IsChromiumFamily);
        Assert.Null(result.ExecutablePath);
    }

    [Theory]
    [InlineData(
        "\"C:\\Program Files (x86)\\Microsoft\\Edge\\Application\\msedge.exe\" --single-argument %1",
        "C:\\Program Files (x86)\\Microsoft\\Edge\\Application\\msedge.exe")]
    [InlineData("\"C:\\App\\v.exe\"", "C:\\App\\v.exe")]
    [InlineData("C:\\App\\v.exe %1", "C:\\App\\v.exe")]
    public void Map_ParsesExePathFromQuotedAndUnquotedCommands(string raw, string expectedExe)
    {
        var result = DefaultBrowser.Map("ChromeHTML", raw);
        Assert.Equal(expectedExe, result.ExecutablePath);
    }

    [Fact]
    public void Detect_DoesNotThrowAndReturnsUnknownOffWindows()
    {
        // Detect always returns a value, even on non-Windows; on Windows it
        // returns whatever the live registry says. We just assert it doesn't
        // throw and the friendly-name field is populated.
        var result = DefaultBrowser.Detect();
        Assert.False(string.IsNullOrEmpty(result.FriendlyName));
    }
}
