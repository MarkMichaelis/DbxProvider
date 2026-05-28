using System.Collections.Generic;
using DbxProvider.Cmdlets;
using DbxProvider.Services;
using Xunit;

namespace DbxProvider.UnitTests;

/// <summary>
/// Tests for <see cref="ConnectDropboxCommand.PickFallbackApp"/>, which
/// supplies a previously-saved AppKey/AppSecret when the caller asks to
/// authenticate a brand-new Dropbox account and didn't pass -AppKey on the
/// command line. Reusing the AppKey lets the same Dropbox app authenticate
/// any number of users without re-pasting -AppKey for each new account.
///
/// Critical invariant: the helper must NEVER surface a RefreshToken —
/// that belongs to another user.
/// </summary>
public class ConnectDropboxFallbackAppTests
{
    [Fact]
    public void PickFallbackApp_ReturnsNull_WhenNoSavedAccounts()
    {
        var result = ConnectDropboxCommand.PickFallbackApp(new List<StoredAccountEntry>());
        Assert.Null(result);
    }

    [Fact]
    public void PickFallbackApp_ReturnsNull_WhenNoSavedAccountHasAppKey()
    {
        var accounts = new List<StoredAccountEntry>
        {
            Entry(key: "dbid:A", email: "a@x.com", appKey: null, isDefault: true),
            Entry(key: "dbid:B", email: "b@x.com", appKey: null, isDefault: false),
        };
        Assert.Null(ConnectDropboxCommand.PickFallbackApp(accounts));
    }

    [Fact]
    public void PickFallbackApp_PrefersDefaultAccountAppKey()
    {
        var accounts = new List<StoredAccountEntry>
        {
            Entry(key: "dbid:A", email: "a@x.com", appKey: "key-a", appSecret: "sec-a", isDefault: false),
            Entry(key: "dbid:B", email: "b@x.com", appKey: "key-b", appSecret: "sec-b", isDefault: true),
            Entry(key: "dbid:C", email: "c@x.com", appKey: "key-c", appSecret: "sec-c", isDefault: false),
        };

        var result = ConnectDropboxCommand.PickFallbackApp(accounts);

        Assert.NotNull(result);
        Assert.Equal("key-b", result!.Value.AppKey);
        Assert.Equal("sec-b", result.Value.AppSecret);
        Assert.Equal("b@x.com", result.Value.SourceLabel);
    }

    [Fact]
    public void PickFallbackApp_FallsBackToFirstAvailable_WhenDefaultHasNoAppKey()
    {
        // Default account exists but doesn't have an AppKey (e.g. it was
        // created via an access-token-only Connect-Dropbox). Helper should
        // skip it and pick the next account that does.
        var accounts = new List<StoredAccountEntry>
        {
            Entry(key: "dbid:A", email: "a@x.com", appKey: null,    isDefault: true),
            Entry(key: "dbid:B", email: "b@x.com", appKey: "key-b", appSecret: "sec-b", isDefault: false),
            Entry(key: "dbid:C", email: "c@x.com", appKey: "key-c", isDefault: false),
        };

        var result = ConnectDropboxCommand.PickFallbackApp(accounts);

        Assert.NotNull(result);
        Assert.Equal("key-b", result!.Value.AppKey);
        Assert.Equal("sec-b", result.Value.AppSecret);
    }

    [Fact]
    public void PickFallbackApp_FallsBackToFirstAvailable_WhenNoDefault()
    {
        var accounts = new List<StoredAccountEntry>
        {
            Entry(key: "dbid:A", email: "a@x.com", appKey: "key-a", appSecret: "sec-a", isDefault: false),
            Entry(key: "dbid:B", email: "b@x.com", appKey: "key-b", appSecret: "sec-b", isDefault: false),
        };

        var result = ConnectDropboxCommand.PickFallbackApp(accounts);

        Assert.NotNull(result);
        Assert.Equal("key-a", result!.Value.AppKey);
    }

    [Fact]
    public void PickFallbackApp_UsesKeyAsLabel_WhenEmailMissing()
    {
        // Pre-auth or legacy entries may have no email — fall back to the
        // dictionary key (e.g. "default" sentinel or a raw accountId).
        var accounts = new List<StoredAccountEntry>
        {
            Entry(key: "default", email: null, appKey: "key-x", isDefault: true),
        };

        var result = ConnectDropboxCommand.PickFallbackApp(accounts);

        Assert.NotNull(result);
        Assert.Equal("default", result!.Value.SourceLabel);
    }

    [Fact]
    public void PickFallbackApp_DoesNotExposeRefreshToken()
    {
        // The struct contract: only AppKey + AppSecret + SourceLabel are
        // exposed. This test guards against accidentally adding a
        // RefreshToken property and reusing it across users.
        var fields = typeof(ConnectDropboxCommand.FallbackApp).GetProperties();
        var fieldNames = new HashSet<string>();
        foreach (var f in fields) fieldNames.Add(f.Name);

        Assert.Contains("AppKey",      fieldNames);
        Assert.Contains("AppSecret",   fieldNames);
        Assert.Contains("SourceLabel", fieldNames);
        Assert.DoesNotContain("RefreshToken", fieldNames);
        Assert.DoesNotContain("AccessToken",  fieldNames);
    }

    private static StoredAccountEntry Entry(
        string key, string? email, string? appKey,
        string? appSecret = null, bool isDefault = false)
    {
        var account = new StoredAccount
        {
            AccountId    = key.StartsWith("dbid:") ? key : null,
            Email        = email,
            AppKey       = appKey,
            AppSecret    = appSecret,
            RefreshToken = "rt-should-never-leak-" + key,
        };
        return new StoredAccountEntry(key, account, isDefault);
    }
}
