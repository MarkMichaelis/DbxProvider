using System;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Dropbox.Api;

namespace MarkMichaelis.Dropbox.Auth;

/// <summary>
/// The result of the loopback OAuth redirect: the authorization
/// <paramref name="Code"/>, the echoed <paramref name="State"/>, and any
/// <paramref name="Error"/> reported by the authorization server.
/// </summary>
/// <param name="Code">The authorization code, when the flow succeeded.</param>
/// <param name="State">The CSRF state value echoed back by Dropbox.</param>
/// <param name="Error">The error code, when authorization failed or was denied.</param>
public sealed record OAuthCallback(string? Code, string? State, string? Error);

/// <summary>
/// Runs Dropbox's OAuth 2 authorization-code flow with PKCE (S256) over a
/// localhost loopback redirect, requesting <c>token_access_type=offline</c> so a
/// long-lived refresh token is issued. Opens the system browser, listens on the
/// loopback <see cref="HttpListener"/> for the redirect, validates the CSRF
/// <c>state</c>, and exchanges the code for tokens  returning a
/// <see cref="DropboxCredential"/>.
///
/// The browser-listen and code-exchange steps are injectable seams used by unit
/// tests; production uses an <see cref="HttpListener"/> and
/// <see cref="DropboxOAuth2Helper"/> respectively.
/// </summary>
public sealed class LoopbackOAuthFlow
{
    private readonly IConsole _console;
    private readonly Func<Uri, string, string, CancellationToken, Task<OAuthCallback>> _listen;
    private readonly Func<string, string, string?, string, string, CancellationToken, Task<DropboxCredential>> _exchange;

    /// <summary>
    /// Creates a flow that surfaces progress through <paramref name="console"/>
    /// and uses the real loopback listener + Dropbox token exchange.
    /// </summary>
    public LoopbackOAuthFlow(IConsole console)
        : this(console, null, null)
    {
    }

    internal LoopbackOAuthFlow(
        IConsole console,
        Func<Uri, string, string, CancellationToken, Task<OAuthCallback>>? listen,
        Func<string, string, string?, string, string, CancellationToken, Task<DropboxCredential>>? exchange)
    {
        _console = console ?? throw new ArgumentNullException(nameof(console));
        _listen = listen ?? ListenForCallbackAsync;
        _exchange = exchange ?? ExchangeCodeAsync;
    }

    /// <summary>
    /// Executes the full flow and returns the resulting credential.
    /// </summary>
    /// <param name="appKey">The Dropbox app key (client id).</param>
    /// <param name="appSecret">The optional app secret (confidential apps only).</param>
    /// <param name="port">The loopback TCP port to bind for the redirect.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="InvalidOperationException">On OAuth error or CSRF state mismatch.</exception>
    public async Task<DropboxCredential> RunAsync(
        string appKey, string? appSecret, int port, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(appKey)) throw new ArgumentNullException(nameof(appKey));

        var redirectUri = $"http://localhost:{port.ToString(CultureInfo.InvariantCulture)}/";
        var state = Guid.NewGuid().ToString("N");

        var codeVerifier = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
        var codeChallenge = ComputeS256Challenge(codeVerifier);
        var authorizeUri = BuildAuthorizeUri(appKey, redirectUri, state, codeChallenge);

        var callback = await _listen(authorizeUri, redirectUri, state, ct).ConfigureAwait(false);

        if (!string.IsNullOrEmpty(callback.Error))
            throw new InvalidOperationException($"OAuth flow returned error: {callback.Error}");

        if (callback.State != state)
            throw new InvalidOperationException("OAuth state mismatch. Possible CSRF attack.");

        if (string.IsNullOrEmpty(callback.Code))
            throw new InvalidOperationException("No authorization code received.");

        return await _exchange(callback.Code!, appKey, appSecret, redirectUri, codeVerifier, ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Builds the Dropbox authorize URI with PKCE (S256) and offline access.
    /// </summary>
    public static Uri BuildAuthorizeUri(string appKey, string redirectUri, string state, string codeChallenge)
        => new Uri(
            "https://www.dropbox.com/oauth2/authorize" +
            $"?client_id={Uri.EscapeDataString(appKey)}" +
            "&response_type=code" +
            $"&redirect_uri={Uri.EscapeDataString(redirectUri)}" +
            $"&state={state}" +
            $"&code_challenge={codeChallenge}" +
            "&code_challenge_method=S256" +
            "&token_access_type=offline");

    /// <summary>Computes the base64url-encoded SHA-256 PKCE challenge.</summary>
    public static string ComputeS256Challenge(string codeVerifier)
    {
        using var sha256 = SHA256.Create();
        var challengeBytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(codeVerifier));
        return Convert.ToBase64String(challengeBytes)
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private async Task<OAuthCallback> ListenForCallbackAsync(
        Uri authorizeUri, string redirectUri, string state, CancellationToken ct)
    {
        _console.Info("Opening browser for Dropbox authorization...");
        _console.Info($"If browser doesn't open, visit: {authorizeUri}");

        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo(authorizeUri.ToString())
            { UseShellExecute = true };
            System.Diagnostics.Process.Start(psi);
        }
        catch
        {
            // Browser may not open in some environments; user can copy the URL.
        }

        using var listener = new HttpListener();
        try
        {
            listener.Prefixes.Add(redirectUri);
            listener.Start();
        }
        catch (HttpListenerException ex)
        {
            throw new InvalidOperationException(
                $"Failed to bind OAuth callback listener on {redirectUri}. " +
                "Choose a different port via -RedirectPort and register http://localhost:<port>/ " +
                $"in your Dropbox app's redirect URIs. Underlying error: {ex.Message}", ex);
        }

        _console.Info($"Waiting for authorization on {redirectUri} ...");
        _console.Info("(Press Ctrl+C to cancel.)");

        string? code = null;
        string? returnedState = null;
        string? errorParam = null;

        // Browsers may probe the listener (favicon, preconnect, HSTS) before delivering the
        // OAuth redirect. Skip such requests until we see one carrying ?code= or ?error=.
        while (true)
        {
            var contextTask = listener.GetContextAsync();
            while (!contextTask.IsCompleted)
            {
                if (ct.IsCancellationRequested)
                {
                    listener.Stop();
                    ct.ThrowIfCancellationRequested();
                }
                if (contextTask.Wait(TimeSpan.FromMilliseconds(150)))
                    break;
            }

            var context = contextTask.Result;
            code = context.Request.QueryString["code"];
            returnedState = context.Request.QueryString["state"];
            errorParam = context.Request.QueryString["error"];

            var hasOAuthPayload = !string.IsNullOrEmpty(code) || !string.IsNullOrEmpty(errorParam);

            string responseHtml;
            if (!hasOAuthPayload)
            {
                // 404 the probe so the browser doesn't cache it as our success page.
                context.Response.StatusCode = 404;
                responseHtml = "<html><body>Waiting for OAuth callback...</body></html>";
            }
            else if (!string.IsNullOrEmpty(errorParam))
            {
                responseHtml = $"<html><body><h2>Authorization failed</h2><p>{WebUtility.HtmlEncode(errorParam)}</p></body></html>";
            }
            else
            {
                responseHtml = "<html><body><h2>Authorization successful!</h2><p>You can close this window.</p></body></html>";
            }

            var buffer = Encoding.UTF8.GetBytes(responseHtml);
            context.Response.ContentLength64 = buffer.Length;
            context.Response.OutputStream.Write(buffer, 0, buffer.Length);
            context.Response.Close();

            if (hasOAuthPayload) break;
        }
        listener.Stop();

        return new OAuthCallback(code, returnedState, errorParam);
    }

    private static async Task<DropboxCredential> ExchangeCodeAsync(
        string code, string appKey, string? appSecret, string redirectUri, string codeVerifier, CancellationToken ct)
    {
        var tokenResult = await DropboxOAuth2Helper.ProcessCodeFlowAsync(
            code, appKey, appSecret, redirectUri, null, codeVerifier).ConfigureAwait(false);

        return new DropboxCredential(
            appKey, appSecret, tokenResult.RefreshToken, tokenResult.AccessToken);
    }
}
