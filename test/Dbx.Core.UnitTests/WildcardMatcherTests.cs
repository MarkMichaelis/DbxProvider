using Dbx.Core.Wildcards;
using FluentAssertions;
using Xunit;

namespace Dbx.Core.UnitTests;

/// <summary>
/// Characterization tests pinning the framework-neutral <see cref="WildcardMatcher"/>
/// to PowerShell <c>WildcardPattern</c> (IgnoreCase) semantics: whole-string anchored
/// match, <c>*</c>/<c>?</c>/<c>[set]</c> operators, backtick escaping, literal treatment
/// of regex metacharacters, and case-insensitivity.
/// </summary>
public class WildcardMatcherTests
{
    [Theory]
    // Star: zero-or-more, anchored over the whole string.
    [InlineData("*", "anything", true)]
    [InlineData("*", "", true)]
    [InlineData("*.txt", "file.txt", true)]
    [InlineData("*.txt", "file.csv", false)]
    [InlineData("*.txt", "file.txtx", false)]
    [InlineData("report*", "report2024.pdf", true)]
    [InlineData("report*", "myreport", false)]
    // Question mark: exactly one character.
    [InlineData("file?.txt", "file1.txt", true)]
    [InlineData("file?.txt", "file.txt", false)]
    [InlineData("file?.txt", "file12.txt", false)]
    // Character sets and ranges.
    [InlineData("[abc]at", "bat", true)]
    [InlineData("[abc]at", "cat", true)]
    [InlineData("[abc]at", "dat", false)]
    [InlineData("[a-c]at", "bat", true)]
    [InlineData("[a-c]at", "dat", false)]
    public void IsMatch_StarQuestionAndSets_MatchWildcardPatternSemantics(
        string pattern, string input, bool expected)
    {
        new WildcardMatcher(pattern).IsMatch(input).Should().Be(expected);
    }

    [Theory]
    // Case-insensitivity (IgnoreCase).
    [InlineData("ABC", "abc", true)]
    [InlineData("*.TXT", "file.txt", true)]
    [InlineData("[A-C]at", "bat", true)]
    public void IsMatch_IgnoreCase_MatchesRegardlessOfCase(
        string pattern, string input, bool expected)
    {
        new WildcardMatcher(pattern).IsMatch(input).Should().Be(expected);
    }

    [Theory]
    // Backtick is the PowerShell wildcard escape character.
    [InlineData("file`*.txt", "file*.txt", true)]
    [InlineData("file`*.txt", "fileX.txt", false)]
    [InlineData("`?", "?", true)]
    [InlineData("`?", "a", false)]
    [InlineData("a`[b`]", "a[b]", true)]
    public void IsMatch_BacktickEscape_MatchesLiteralWildcardChars(
        string pattern, string input, bool expected)
    {
        new WildcardMatcher(pattern).IsMatch(input).Should().Be(expected);
    }

    [Theory]
    // Regex metacharacters are literals in wildcard syntax.
    [InlineData("a.b", "a.b", true)]
    [InlineData("a.b", "axb", false)]
    [InlineData("a+b", "a+b", true)]
    [InlineData("a+b", "ab", false)]
    [InlineData("a(b)", "a(b)", true)]
    [InlineData("a$b", "a$b", true)]
    public void IsMatch_RegexMetacharacters_TreatedAsLiterals(
        string pattern, string input, bool expected)
    {
        new WildcardMatcher(pattern).IsMatch(input).Should().Be(expected);
    }
}