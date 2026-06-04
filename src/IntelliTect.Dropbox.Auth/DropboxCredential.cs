namespace IntelliTect.Dropbox.Auth;

/// <summary>
/// A Dropbox OAuth credential. <see cref="RefreshToken"/> is present after a
/// successful offline-access PKCE flow; <see cref="AccessToken"/> carries the
/// short-lived token when no refresh token was issued.
/// </summary>
/// <param name="AppKey">The Dropbox app key (client id).</param>
/// <param name="AppSecret">The optional app secret (confidential apps only).</param>
/// <param name="RefreshToken">The long-lived refresh token, when issued.</param>
/// <param name="AccessToken">The short-lived access token, when issued.</param>
public sealed record DropboxCredential(
    string AppKey,
    string? AppSecret,
    string? RefreshToken,
    string? AccessToken);
