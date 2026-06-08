using FluentAssertions;
using IntelliTect.Dropbox;
using Xunit;

namespace Dbx.Core.UnitTests;

/// <summary>
/// Behavior tests for <see cref="LegacyConflictScanState.FromJson"/> -- the
/// shape check that decides whether a <c>-StatePath</c> file is an obsolete
/// conflict-scan sidecar worth migrating, or an unrelated JSON file that must be
/// left untouched.
/// </summary>
public sealed class LegacyConflictScanStateTests
{
    private const string ValidSidecar = @"{
  ""AccountId"": ""acct-123"",
  ""StartPath"": """",
  ""Pattern"": ""*'s conflicted copy*"",
  ""IncludeNonZero"": false,
  ""Cursor"": ""cursor-abc"",
  ""Matches"": { ""/a/x.txt"": { ""Path"": ""/A/x.txt"", ""Bytes"": 0 } }
}";

    [Fact]
    public void FromJson_ValidLegacyShape_ReturnsParsedState()
    {
        var state = LegacyConflictScanState.FromJson(ValidSidecar);

        state.Should().NotBeNull();
        state!.AccountId.Should().Be("acct-123");
        state.Cursor.Should().Be("cursor-abc");
        state.Matches.Should().ContainKey("/a/x.txt");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json at all")]
    public void FromJson_BlankOrInvalid_ReturnsNull(string? json)
    {
        LegacyConflictScanState.FromJson(json).Should().BeNull();
    }

    [Fact]
    public void FromJson_EmptyObject_ReturnsNull()
    {
        // A bare {} is valid JSON but is not a sidecar; it must not be migrated.
        LegacyConflictScanState.FromJson("{}").Should().BeNull();
    }

    [Fact]
    public void FromJson_MissingCursor_ReturnsNull()
    {
        LegacyConflictScanState.FromJson(@"{ ""AccountId"": ""acct-123"" }")
            .Should().BeNull();
    }

    [Fact]
    public void FromJson_MissingAccountId_ReturnsNull()
    {
        LegacyConflictScanState.FromJson(@"{ ""Cursor"": ""cursor-abc"" }")
            .Should().BeNull();
    }

    [Fact]
    public void FromJson_MatchesNull_IsNormalizedToEmptyDictionary()
    {
        // "Matches": null would otherwise null out the property and make a
        // migrating caller's Matches.Count throw.
        var state = LegacyConflictScanState.FromJson(
            @"{ ""AccountId"": ""acct-123"", ""Cursor"": ""cursor-abc"", ""Matches"": null }");

        state.Should().NotBeNull();
        state!.Matches.Should().NotBeNull();
        state.Matches.Count.Should().Be(0);
    }
}