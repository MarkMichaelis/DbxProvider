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

    [Theory]
    // Drive-qualified provider paths produced by WriteDropboxItem / WriteItemObject
    // when an item is piped from a provider/cmdlet into a Dropbox API cmdlet. The
    // drive qualifier (e.g. "Dbx:") must be stripped so the API receives a real
    // Dropbox path -- without this the path became "/Dbx:/A/b.txt", which the API
    // rejects, so "Search-Dropbox 'x' | Get-DropboxRevision" returned nothing.
    [InlineData("Dbx:\\A\\b.txt")]
    [InlineData("Dbx:/A/b.txt")]
    [InlineData("Dbx:A\\b.txt")]
    public void DriveQualified_StripsDrivePrefix(string input)
    {
        DropboxServiceClient.NormalizePath(input).Should().Be("/A/b.txt");
    }

    [Theory]
    [InlineData("Dbx:")]
    [InlineData("Dbx:\\")]
    public void DriveQualifiedRoot_NormalizesToEmpty(string input)
    {
        DropboxServiceClient.NormalizePath(input).Should().Be("");
    }
}
