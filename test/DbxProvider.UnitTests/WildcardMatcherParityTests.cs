using System.Management.Automation;
using Dbx.Core.Wildcards;
using Xunit;

namespace DbxProvider.UnitTests;

/// <summary>
/// Characterization parity: the framework-neutral <see cref="WildcardMatcher"/>
/// must produce identical results to PowerShell's <see cref="WildcardPattern"/>
/// (IgnoreCase) across the in-scope feature set (*, ?, [set], [range], backtick
/// escape, literal metacharacters). This guards the WildcardPattern -> Core
/// matcher swap in DropboxServiceClient.SearchByFilenameAsync.
/// </summary>
public class WildcardMatcherParityTests
{
    private static readonly string[] Inputs =
    {
        "report.txt", "Report.TXT", "report2024.pdf", "myreport", "a.b", "axb",
        "a+b", "ab", "file.txt", "file1.txt", "file12.txt", "bat", "cat", "dat",
        "BAT", "file*.txt", "fileX.txt", "", "notes", "image.PNG", "a(b)", "a$b",
    };

    [Theory]
    [InlineData("*")]
    [InlineData("*.txt")]
    [InlineData("*.TXT")]
    [InlineData("report*")]
    [InlineData("file?.txt")]
    [InlineData("[abc]at")]
    [InlineData("[a-c]at")]
    [InlineData("ABC")]
    [InlineData("a.b")]
    [InlineData("a+b")]
    [InlineData("file`*.txt")]
    [InlineData("`?")]
    public void IsMatch_AgreesWithWildcardPattern(string pattern)
    {
        var matcher = new WildcardMatcher(pattern);
        var reference = new WildcardPattern(pattern, WildcardOptions.IgnoreCase);

        foreach (var input in Inputs)
        {
            Assert.Equal(reference.IsMatch(input), matcher.IsMatch(input));
        }
    }
}