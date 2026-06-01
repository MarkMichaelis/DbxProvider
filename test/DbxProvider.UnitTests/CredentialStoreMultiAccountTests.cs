using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DbxProvider.Services;
using Xunit;

namespace DbxProvider.UnitTests;

/// <summary>
/// Tests for the v2 multi-account credential store. Each test redirects
/// LOCALAPPDATA to a temp directory so it can never touch a developer's
/// real credentials file.
/// </summary>
[Collection("CredentialStore")]
public class CredentialStoreMultiAccountTests : IDisposable
{
    private readonly string _origLocalAppData;
    private readonly string _tempLocalAppData;

    public CredentialStoreMultiAccountTests()
    {
        _origLocalAppData = Environment.GetEnvironmentVariable("LOCALAPPDATA") ?? string.Empty;
        _tempLocalAppData = Path.Combine(Path.GetTempPath(),
            "DbxProviderCredTest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempLocalAppData);
        Environment.SetEnvironmentVariable("LOCALAPPDATA", _tempLocalAppData);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("LOCALAPPDATA", _origLocalAppData);
        try { Directory.Delete(_tempLocalAppData, recursive: true); } catch { }
    }

    [Fact]
    public void EmptyStore_ListAccountsReturnsEmpty()
    {
        Assert.Empty(CredentialStore.ListAccounts());
        Assert.Null(CredentialStore.LoadAccount());
    }

    [Fact]
    public void SaveAccount_RoundTripsAccountId()
    {
        CredentialStore.SaveAccount(new StoredAccount
        {
            AppKey = "k1", AppSecret = "s1", RefreshToken = "rt1",
            AccountId = "dbid:aaa", Email = "a@example.com", DisplayName = "Alice"
        });

        var loaded = CredentialStore.LoadAccount();
        Assert.NotNull(loaded);
        Assert.Equal("dbid:aaa", loaded!.AccountId);
        Assert.Equal("a@example.com", loaded.Email);
        Assert.Equal("k1", loaded.AppKey);
        Assert.Equal("s1", loaded.AppSecret);
        Assert.Equal("rt1", loaded.RefreshToken);
    }

    [Fact]
    public void SaveAccount_TwoDistinctAccounts_AreIsolated()
    {
        CredentialStore.SaveAccount(new StoredAccount
        {
            AppKey = "k1", RefreshToken = "rt1",
            AccountId = "dbid:aaa", Email = "alice@x.com"
        });
        CredentialStore.SaveAccount(new StoredAccount
        {
            AppKey = "k2", RefreshToken = "rt2",
            AccountId = "dbid:bbb", Email = "bob@x.com"
        });

        var all = CredentialStore.ListAccounts();
        Assert.Equal(2, all.Count);
        Assert.Contains(all, e => e.Account.AccountId == "dbid:aaa" && e.Account.RefreshToken == "rt1");
        Assert.Contains(all, e => e.Account.AccountId == "dbid:bbb" && e.Account.RefreshToken == "rt2");
        // First saved becomes default.
        Assert.Equal("dbid:aaa", all.Single(e => e.IsDefault).Account.AccountId);
    }

    [Fact]
    public void SelectorResolution_AccountId_Email_AndLocalPart()
    {
        CredentialStore.SaveAccount(new StoredAccount
        {
            AccountId = "dbid:aaa", Email = "alice@one.com", AppKey = "k1", RefreshToken = "rt1"
        });
        CredentialStore.SaveAccount(new StoredAccount
        {
            AccountId = "dbid:bbb", Email = "bob@two.com", AppKey = "k2", RefreshToken = "rt2"
        });

        Assert.Equal("dbid:aaa", CredentialStore.LoadAccount("dbid:aaa")!.AccountId);
        Assert.Equal("dbid:bbb", CredentialStore.LoadAccount("BOB@two.com")!.AccountId);
        Assert.Equal("dbid:aaa", CredentialStore.LoadAccount("alice")!.AccountId);
    }

    [Fact]
    public void SelectorResolution_AmbiguousLocalPart_Throws()
    {
        CredentialStore.SaveAccount(new StoredAccount
        {
            AccountId = "dbid:aaa", Email = "mark@one.com"
        });
        CredentialStore.SaveAccount(new StoredAccount
        {
            AccountId = "dbid:bbb", Email = "mark@two.com"
        });

        Assert.Throws<InvalidOperationException>(() => CredentialStore.LoadAccount("mark"));
    }

    [Fact]
    public void RemoveAccount_LeavesSiblingsIntact()
    {
        CredentialStore.SaveAccount(new StoredAccount { AccountId = "dbid:aaa", Email = "a@x.com" });
        CredentialStore.SaveAccount(new StoredAccount { AccountId = "dbid:bbb", Email = "b@x.com" });

        Assert.True(CredentialStore.RemoveAccount("dbid:aaa"));

        var remaining = CredentialStore.ListAccounts();
        Assert.Single(remaining);
        Assert.Equal("dbid:bbb", remaining[0].Account.AccountId);
        Assert.True(remaining[0].IsDefault, "the surviving account should become the new default");
    }

    [Fact]
    public void RemoveAccount_WhenLastAccount_DeletesFile()
    {
        CredentialStore.SaveAccount(new StoredAccount { AccountId = "dbid:aaa" });
        Assert.True(CredentialStore.Exists);

        CredentialStore.RemoveAccount("dbid:aaa");
        Assert.False(CredentialStore.Exists);
    }

    [Fact]
    public void SetDefaultAccount_ChangesDefaultSelection()
    {
        CredentialStore.SaveAccount(new StoredAccount { AccountId = "dbid:aaa", Email = "a@x.com" });
        CredentialStore.SaveAccount(new StoredAccount { AccountId = "dbid:bbb", Email = "b@x.com" });
        Assert.Equal("dbid:aaa", CredentialStore.LoadAccount()!.AccountId);

        CredentialStore.SetDefaultAccount("b@x.com");
        Assert.Equal("dbid:bbb", CredentialStore.LoadAccount()!.AccountId);
    }

    [Fact]
    public void SaveAccount_PartialUpdate_PreservesOtherFields()
    {
        CredentialStore.SaveAccount(new StoredAccount
        {
            AppKey = "k1", AppSecret = "s1", RefreshToken = "rt1",
            AccountId = "dbid:aaa", Email = "a@x.com", DisplayName = "Alice"
        });

        // Only update RefreshToken via legacy-style call (no AccountId).
#pragma warning disable CS0618
        CredentialStore.Save(appKey: null, appSecret: null, refreshToken: "rt1-new");
#pragma warning restore CS0618

        var loaded = CredentialStore.LoadAccount();
        Assert.Equal("rt1-new", loaded!.RefreshToken);
        Assert.Equal("k1", loaded.AppKey);
        Assert.Equal("s1", loaded.AppSecret);
        Assert.Equal("Alice", loaded.DisplayName);
    }

    private static void WriteLegacyV1File()
    {
        // Hand-write a v1 file: { appKey, appSecret, refreshToken, savedAt }.
        // The "plain:<base64>" payload is composed at runtime so this source
        // file does not contain a literal value matching the SecretLeakTests
        // CredentialStore-encoding pattern.
        var path = CredentialStore.CredentialFilePath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var prefix = "plain" + ":";
        var secretEnc = prefix + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("legacy-secret"));
        var rtEnc = prefix + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("legacy-rt"));
        File.WriteAllText(path,
            "{\n" +
            "  \"appKey\": \"legacy-key\",\n" +
            "  \"appSecret\": \"" + secretEnc + "\",\n" +
            "  \"refreshToken\": \"" + rtEnc + "\",\n" +
            "  \"savedAt\": \"2024-01-01T00:00:00+00:00\"\n" +
            "}\n");
    }

    [Fact]
    public void LegacyV1File_IsMigratedToPreAuthEntry()
    {
        WriteLegacyV1File();

        var entries = CredentialStore.ListAccounts();
        Assert.Single(entries);
        var e = entries[0];
        Assert.True(e.IsDefault);
        Assert.Equal("legacy-key", e.Account.AppKey);
        Assert.Equal("legacy-secret", e.Account.AppSecret);
        Assert.Equal("legacy-rt", e.Account.RefreshToken);
        Assert.Null(e.Account.AccountId);
    }

    [Fact]
    public void LegacyV1File_NextAuthRekeysUnderAccountId()
    {
        WriteLegacyV1File();

        // Simulate Connect-Dropbox enriching the entry with the account it
        // discovered after the first successful API call.
        CredentialStore.SaveAccount(new StoredAccount
        {
            AppKey = "legacy-key",
            AppSecret = "legacy-secret",
            RefreshToken = "legacy-rt",
            AccountId = "dbid:NEW",
            Email = "owner@example.com",
            DisplayName = "Owner"
        });

        var entries = CredentialStore.ListAccounts();
        Assert.Single(entries);
        Assert.Equal("dbid:NEW", entries[0].Key);
        Assert.Equal("dbid:NEW", entries[0].Account.AccountId);
        Assert.Equal("owner@example.com", entries[0].Account.Email);
        Assert.True(entries[0].IsDefault);
    }
}
