using System;
using System.IO;
using DbxProvider.Services;
using IntelliTect.Dropbox.Auth;
using Xunit;

namespace DbxProvider.UnitTests;

/// <summary>
/// Verifies the <see cref="DbxCredentialStore"/> adapter maps a
/// <see cref="DropboxCredential"/> onto the existing on-disk
/// <see cref="StoredAccount"/> JSON shape without altering the format: a value
/// saved through the adapter is readable through the underlying
/// <see cref="CredentialStore"/> and round-trips back through the adapter.
/// LOCALAPPDATA is redirected to a temp dir so a developer's real credential
/// file is never touched.
/// </summary>
[Collection("CredentialStore")]
public class DbxCredentialStoreAdapterTests : IDisposable
{
    private readonly string _origLocalAppData;
    private readonly string _tempLocalAppData;

    public DbxCredentialStoreAdapterTests()
    {
        _origLocalAppData = Environment.GetEnvironmentVariable("LOCALAPPDATA") ?? string.Empty;
        _tempLocalAppData = Path.Combine(Path.GetTempPath(),
            "DbxProviderCredAdapterTest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempLocalAppData);
        Environment.SetEnvironmentVariable("LOCALAPPDATA", _tempLocalAppData);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("LOCALAPPDATA", _origLocalAppData);
        try { Directory.Delete(_tempLocalAppData, recursive: true); } catch { }
    }

    [Fact]
    public void Save_PersistsThroughExistingCredentialStoreShape()
    {
        var adapter = new DbxCredentialStore();
        adapter.Save(new DropboxCredential("app-key-1", "app-secret-1", "refresh-1", AccessToken: "ignored"));

        // The value is readable through the existing multi-account store, proving
        // the on-disk StoredAccount JSON shape is preserved (and the volatile
        // access token is not persisted).
        var stored = CredentialStore.LoadAccount();
        Assert.NotNull(stored);
        Assert.Equal("app-key-1", stored!.AppKey);
        Assert.Equal("app-secret-1", stored.AppSecret);
        Assert.Equal("refresh-1", stored.RefreshToken);
    }

    [Fact]
    public void Load_RoundTripsCredentialAndDropsAccessToken()
    {
        var adapter = new DbxCredentialStore();
        adapter.Save(new DropboxCredential("k2", "s2", "rt2", AccessToken: "short-lived"));

        // An empty key resolves to the default account (the pre-auth entry a
        // credential without account identity is stored under).
        var loaded = adapter.Load(key: string.Empty);

        Assert.NotNull(loaded);
        Assert.Equal("k2", loaded!.AppKey);
        Assert.Equal("s2", loaded.AppSecret);
        Assert.Equal("rt2", loaded.RefreshToken);
        Assert.Null(loaded.AccessToken);
    }
}
