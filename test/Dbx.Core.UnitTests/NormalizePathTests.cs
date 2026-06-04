using IntelliTect.Dropbox;
using FluentAssertions;
using Xunit;

namespace Dbx.Core.UnitTests;

/// <summary>
/// Verifies <see cref="DropboxServiceClient.NormalizePath"/> maps the various
/// PowerShell provider path forms the Dropbox provider receives into the
/// canonical Dropbox path (forward slashes, single leading slash, no trailing
/// slash). PowerShell strips the drive (<c>Dbx:</c>) and provider-qualified
/// (<c>DbxProvider\Dropbox::</c>) prefixes before calling the provider, so the
/// provider sees relative/leading-slash/backslash forms; all must round-trip to
/// the same Dropbox path so an item's <c>PSPath</c> resolves to the right place.
/// </summary>
public class NormalizePathTests
{
    [Theory]
    [InlineData("")]
    [InlineData("\\")]
    [InlineData("/")]
    [InlineData(".")]
    public void Root_Forms_NormalizeToEmpty(string input)
    {
        DropboxServiceClient.NormalizePath(input).Should().Be("");
    }

    [Theory]
    // Drive-relative (what PowerShell passes for a provider-qualified PSPath).
    [InlineData("Foo\\Bar")]
    // Leading separator (what PowerShell passes for a drive-rooted path).
    [InlineData("\\Foo\\Bar")]
    [InlineData("/Foo/Bar")]
    // Already-Dropbox form.
    [InlineData("/Foo/Bar/")]
    [InlineData("\\Foo\\Bar\\")]
    public void NestedForms_NormalizeToDropboxPath(string input)
    {
        DropboxServiceClient.NormalizePath(input).Should().Be("/Foo/Bar");
    }

    [Fact]
    public void SingleSegment_GetsLeadingSlash()
    {
        DropboxServiceClient.NormalizePath("Foo").Should().Be("/Foo");
        DropboxServiceClient.NormalizePath("\\Foo").Should().Be("/Foo");
    }

    [Fact]
    public void Backslashes_ConvertToForwardSlashes()
    {
        DropboxServiceClient.NormalizePath("A\\b.txt").Should().Be("/A/b.txt");
    }
}
