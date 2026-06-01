using System;
using System.Threading;
using System.Threading.Tasks;
using MarkMichaelis.Dropbox.Auth;
using Xunit;

namespace Dbx.Auth.UnitTests;

/// <summary>
/// Unit tests for <see cref="LoopbackOAuthFlow"/> using injected fakes for the
/// loopback redirect listener and the code-for-token exchange, so the PKCE
/// orchestration (state validation, error handling, credential shaping) is
/// verified without a real browser, HttpListener, or Dropbox round-trip.
/// </summary>
public class LoopbackOAuthFlowTests
{
    private sealed class RecordingConsole : IConsole
    {
        public System.Collections.Generic.List<string> Lines { get; } = new();
        public void Info(string message) => Lines.Add(message);
        public string Prompt(string message) => string.Empty;
    }

    [Fact]
    public async Task RunAsync_HappyPath_ReturnsCredentialFromExchange()
    {
        string? capturedCode = null;
        var flow = new LoopbackOAuthFlow(
            new RecordingConsole(),
            listen: (authorizeUri, redirectUri, expectedState, ct) =>
                Task.FromResult(new OAuthCallback(Code: "auth-code-123", State: expectedState, Error: null)),
            exchange: (code, appKey, appSecret, redirectUri, verifier, ct) =>
            {
                capturedCode = code;
                return Task.FromResult(new DropboxCredential(appKey, appSecret, "refresh-xyz", "access-abc"));
            });

        var cred = await flow.RunAsync("my-app-key", "my-secret", 52475, CancellationToken.None);

        Assert.Equal("auth-code-123", capturedCode);
        Assert.Equal("my-app-key", cred.AppKey);
        Assert.Equal("my-secret", cred.AppSecret);
        Assert.Equal("refresh-xyz", cred.RefreshToken);
        Assert.Equal("access-abc", cred.AccessToken);
    }

    [Fact]
    public async Task RunAsync_BuildsAuthorizeUriWithPkceAndOfflineAccess()
    {
        Uri? seen = null;
        var flow = new LoopbackOAuthFlow(
            new RecordingConsole(),
            listen: (authorizeUri, redirectUri, expectedState, ct) =>
            {
                seen = authorizeUri;
                return Task.FromResult(new OAuthCallback("code", expectedState, null));
            },
            exchange: (code, appKey, appSecret, redirectUri, verifier, ct) =>
                Task.FromResult(new DropboxCredential(appKey, appSecret, "r", null)));

        await flow.RunAsync("the-key", null, 52475, CancellationToken.None);

        Assert.NotNull(seen);
        var q = seen!.ToString();
        Assert.Contains("client_id=the-key", q);
        Assert.Contains("code_challenge_method=S256", q);
        Assert.Contains("token_access_type=offline", q);
        Assert.Contains("redirect_uri=http%3A%2F%2Flocalhost%3A52475%2F", q);
    }

    [Fact]
    public async Task RunAsync_StateMismatch_ThrowsCsrf()
    {
        var flow = new LoopbackOAuthFlow(
            new RecordingConsole(),
            listen: (authorizeUri, redirectUri, expectedState, ct) =>
                Task.FromResult(new OAuthCallback("code", "tampered-state", null)),
            exchange: (code, appKey, appSecret, redirectUri, verifier, ct) =>
                Task.FromResult(new DropboxCredential(appKey, appSecret, "r", null)));

        var ex = await Assert.ThrowsAnyAsync<Exception>(
            () => flow.RunAsync("k", null, 52475, CancellationToken.None));
        Assert.Contains("state", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RunAsync_ErrorParam_Throws()
    {
        var flow = new LoopbackOAuthFlow(
            new RecordingConsole(),
            listen: (authorizeUri, redirectUri, expectedState, ct) =>
                Task.FromResult(new OAuthCallback(null, expectedState, "access_denied")),
            exchange: (code, appKey, appSecret, redirectUri, verifier, ct) =>
                Task.FromResult(new DropboxCredential(appKey, appSecret, "r", null)));

        var ex = await Assert.ThrowsAnyAsync<Exception>(
            () => flow.RunAsync("k", null, 52475, CancellationToken.None));
        Assert.Contains("access_denied", ex.Message, StringComparison.OrdinalIgnoreCase);
    }
}
