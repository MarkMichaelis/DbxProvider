using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using IntelliTect.Dropbox.Auth;
using Xunit;

namespace Dbx.Auth.UnitTests;

/// <summary>
/// Tests for <see cref="DropboxAppRegistrar.GenerateAppName"/>.
///
/// The Dropbox App Console enforces global app-name uniqueness. We avoid
/// collision-retry logic by generating <c>PSDbxProvider-&lt;8 random alnum&gt;</c>
/// names; with ~2.8 trillion combinations, even 10 000 names should not
/// collide. These tests pin the format and the practical absence of
/// collisions in a 10 000-sample sweep.
/// </summary>
public class DropboxAppRegistrarNamingTests
{
    private static readonly Regex Pattern =
        new(@"^PSDbxProvider-[a-z0-9]{8}$", RegexOptions.Compiled);

    [Fact]
    public void GenerateAppName_MatchesExpectedFormat()
    {
        for (int i = 0; i < 200; i++)
        {
            var name = DropboxAppRegistrar.GenerateAppName();
            Assert.Matches(Pattern, name);
        }
    }

    [Fact]
    public void GenerateAppName_HasNoCollisionsInTenThousandSamples()
    {
        var seen = new HashSet<string>();
        for (int i = 0; i < 10_000; i++)
        {
            var name = DropboxAppRegistrar.GenerateAppName();
            Assert.True(seen.Add(name), $"Collision on '{name}' at iteration {i}");
        }
    }

    [Fact]
    public void GenerateAppName_UsesPSPrefixForAppConsoleVisibility()
    {
        // The Dropbox App Console has no PowerShell context — the PS prefix is
        // how a developer scanning their app list recognises modules they
        // installed from PSGallery.
        var name = DropboxAppRegistrar.GenerateAppName();
        Assert.StartsWith("PSDbxProvider-", name, StringComparison.Ordinal);
    }
}
