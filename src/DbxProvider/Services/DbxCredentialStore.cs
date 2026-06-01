using System;
using MarkMichaelis.Dropbox.Auth;

namespace DbxProvider.Services;

/// <summary>
/// Adapts the library's <see cref="ICredentialStore"/> abstraction onto the
/// existing multi-account <see cref="CredentialStore"/>, mapping a
/// <see cref="DropboxCredential"/> to/from the on-disk
/// <see cref="StoredAccount"/> JSON shape (appKey / appSecret / refreshToken).
/// Short-lived access tokens are intentionally not persisted, matching the
/// store's existing contract.
/// </summary>
public sealed class DbxCredentialStore : ICredentialStore
{
    /// <inheritdoc />
    public void Save(DropboxCredential cred)
    {
        if (cred is null) throw new ArgumentNullException(nameof(cred));
        CredentialStore.SaveAccount(new StoredAccount
        {
            AppKey       = cred.AppKey,
            AppSecret    = cred.AppSecret,
            RefreshToken = cred.RefreshToken,
        });
    }

    /// <inheritdoc />
    /// <remarks>
    /// <paramref name="key"/> follows <see cref="CredentialStore"/> selector
    /// semantics (accountId, email, or unambiguous email local-part); an empty
    /// key resolves to the default account.
    /// </remarks>
    public DropboxCredential? Load(string key)
    {
        var account = CredentialStore.LoadAccount(key);
        if (account is null) return null;

        return new DropboxCredential(
            account.AppKey ?? string.Empty,
            account.AppSecret,
            account.RefreshToken,
            AccessToken: null);
    }
}
