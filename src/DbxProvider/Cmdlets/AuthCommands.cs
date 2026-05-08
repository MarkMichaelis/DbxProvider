using System;
using System.Globalization;
using System.Management.Automation;
using System.Net;
using System.Text;
using Dropbox.Api;
using DbxProvider.Provider;
using DbxProvider.Services;

namespace DbxProvider.Cmdlets
{
    /// <summary>
    /// Authenticates to Dropbox and creates a PSDrive.
    ///
    /// Three usage modes:
    ///  - <c>Connect-Dropbox -AccessToken &lt;token&gt;</c>           : use a short-lived access token directly.
    ///  - <c>Connect-Dropbox -AppKey &lt;key&gt; [-AppSecret ...]</c> : run the OAuth 2 + PKCE browser flow.
    ///  - <c>Connect-Dropbox</c>                                     : reuse credentials saved by a prior run.
    ///
    /// Credentials (AppKey, AppSecret, RefreshToken) are persisted via <see cref="CredentialStore"/>
    /// unless <c>-NoSave</c> is specified.
    /// </summary>
    [Cmdlet(VerbsCommunications.Connect, "Dropbox", DefaultParameterSetName = OAuthSet)]
    [OutputType(typeof(PSDriveInfo))]
    public class ConnectDropboxCommand : PSCmdlet
    {
        public const string TokenSet = "Token";
        public const string OAuthSet = "OAuth";
        public const int DefaultRedirectPort = 52475;

        [Parameter(Mandatory = true, ParameterSetName = TokenSet, Position = 0)]
        public string AccessToken { get; set; } = string.Empty;

        [Parameter(ParameterSetName = OAuthSet)]
        public string? AppKey { get; set; }

        [Parameter(ParameterSetName = OAuthSet)]
        public string? AppSecret { get; set; }

        [Parameter(ParameterSetName = OAuthSet)]
        public string? RefreshToken { get; set; }

        /// <summary>
        /// Selects which saved account's credentials to load (Dropbox accountId,
        /// email, or unambiguous email local-part). When omitted, the default
        /// account is used. When the selector matches no saved account, a
        /// fresh OAuth flow is run (you must pass -AppKey) and the resulting
        /// credentials are persisted under the newly-discovered accountId.
        /// </summary>
        [Parameter(ParameterSetName = OAuthSet)]
        [ArgumentCompleter(typeof(AccountSelectorCompleter))]
        public string? Account { get; set; }

        /// <summary>
        /// Local TCP port the OAuth callback listener binds to. Must match a redirect URI
        /// registered in the Dropbox App Console (e.g. http://localhost:52475/).
        /// </summary>
        [Parameter(ParameterSetName = OAuthSet)]
        [ValidateRange(1, 65535)]
        public int RedirectPort { get; set; } = DefaultRedirectPort;

        /// <summary>If specified, do not persist credentials/refresh token after a successful connect.</summary>
        [Parameter(ParameterSetName = OAuthSet)]
        public SwitchParameter NoSave { get; set; }

        [Parameter]
        public string DriveName { get; set; } = "Dbx";

        protected override void ProcessRecord()
        {
            try
            {
                DropboxServiceClient service;
                string? appKey = AppKey;
                string? appSecret = AppSecret;
                string? refreshToken = RefreshToken;
                bool obtainedNewRefreshToken = false;

                if (ParameterSetName == TokenSet)
                {
                    service = new DropboxServiceClient(AccessToken);
                }
                else
                {
                    StoredAccountEntry? saved = null;
                    try
                    {
                        saved = CredentialStore.ResolveEntry(Account, throwOnAmbiguous: true);
                    }
                    catch (InvalidOperationException ex)
                    {
                        ThrowTerminatingError(new ErrorRecord(ex, "AmbiguousAccount",
                            ErrorCategory.InvalidArgument, Account));
                        return;
                    }

                    appKey       ??= saved?.Account.AppKey;
                    appSecret    ??= saved?.Account.AppSecret;
                    refreshToken ??= saved?.Account.RefreshToken;

                    if (!string.IsNullOrEmpty(refreshToken) && !string.IsNullOrEmpty(appKey))
                    {
                        WriteVerbose(saved != null
                            ? $"Reusing saved refresh token for {saved.Account.Email ?? saved.Account.AccountId ?? saved.Key}."
                            : "Reusing saved refresh token (no browser flow needed).");
                        service = new DropboxServiceClient(refreshToken, appKey, appSecret ?? string.Empty);
                    }
                    else if (!string.IsNullOrEmpty(appKey))
                    {
                        var (newAccessToken, newRefreshToken) = RunOAuthFlow(appKey, appSecret, RedirectPort);
                        if (!string.IsNullOrEmpty(newRefreshToken))
                        {
                            refreshToken = newRefreshToken;
                            obtainedNewRefreshToken = true;
                            service = new DropboxServiceClient(newRefreshToken, appKey, appSecret ?? string.Empty);
                        }
                        else
                        {
                            service = new DropboxServiceClient(newAccessToken);
                        }
                    }
                    else
                    {
                        WriteRegistrationHelp(RedirectPort);
                        throw new InvalidOperationException(
                            "No credentials available. Provide -AccessToken, or -AppKey (with optional -AppSecret), " +
                            "or run Set-DropboxCredential to populate the credential store first.");
                    }
                }

                var account = service.GetCurrentAccountAsync().GetAwaiter().GetResult();
                WriteVerbose($"Authenticated as {account.DisplayName} ({account.Email})");

                if (ParameterSetName == OAuthSet && !NoSave.IsPresent &&
                    (!string.IsNullOrEmpty(appKey) || !string.IsNullOrEmpty(refreshToken)))
                {
                    CredentialStore.SaveAccount(new StoredAccount
                    {
                        AppKey       = appKey,
                        AppSecret    = appSecret,
                        RefreshToken = refreshToken,
                        AccountId    = account.AccountId,
                        Email        = account.Email,
                        DisplayName  = account.DisplayName
                    });
                    if (!string.IsNullOrEmpty(CredentialStore.LastSaveWarning))
                        WriteWarning(CredentialStore.LastSaveWarning);
                    WriteVerbose($"Credentials saved to {CredentialStore.CredentialFilePath}.");
                }

                // Auto-derive a drive name from the account email when the caller
                // did not pass -DriveName explicitly and the request was scoped to
                // a specific account. Keeps multi-account workflows ergonomic
                // (e.g. mark@a.com -> "mark"; collisions append the domain label
                // and then a numeric suffix).
                var effectiveDriveName = DriveName;
                if (!MyInvocation.BoundParameters.ContainsKey("DriveName")
                    && !string.IsNullOrEmpty(Account)
                    && !string.IsNullOrEmpty(account.Email))
                {
                    var derived = DeriveDriveName(account.Email);
                    if (!string.IsNullOrEmpty(derived)) effectiveDriveName = derived;
                }

                var driveInfo = new PSDriveInfo(
                    effectiveDriveName, SessionState.Provider.GetOne("Dropbox"),
                    "\\", $"Dropbox ({account.Email})", null);

                var dbxDrive = new DropboxDriveInfo(driveInfo, service);
                dbxDrive.InitializeCache(account.AccountId);
                SessionState.Drive.New(dbxDrive, "global");
                WriteObject(dbxDrive);

                // Register a global "<DriveName>:" function so users can switch drives by typing
                // "Dbx:" alone, matching the FileSystem provider behavior for "C:".
                try
                {
                    SessionState.InvokeCommand.InvokeScript(
                        false,
                        ScriptBlock.Create($"Set-Item -Path function:global:{effectiveDriveName}: -Value {{ Set-Location -LiteralPath '{effectiveDriveName}:' }}"),
                        null);
                }
                catch (Exception ex)
                {
                    WriteVerbose($"Could not register '{effectiveDriveName}:' shortcut function: {ex.Message}");
                }

                Host.UI.WriteLine($"Connected to Dropbox as {account.DisplayName}. Use '{effectiveDriveName}:' to navigate.");
                if (obtainedNewRefreshToken && NoSave.IsPresent && !string.IsNullOrEmpty(refreshToken))
                {
                    Host.UI.WriteLine($"Refresh token (save for future use): {refreshToken}");
                }
            }
            catch (Exception ex)
            {
                ThrowTerminatingError(new ErrorRecord(ex, "ConnectDropboxFailed",
                    ErrorCategory.AuthenticationError, null));
            }
        }

        private (string AccessToken, string? RefreshToken) RunOAuthFlow(string appKey, string? appSecret, int port)
        {
            var redirectUri = $"http://localhost:{port.ToString(CultureInfo.InvariantCulture)}/";
            var state = Guid.NewGuid().ToString("N");

            var codeVerifier = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
            using var sha256 = System.Security.Cryptography.SHA256.Create();
            var challengeBytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(codeVerifier));
            var codeChallenge = Convert.ToBase64String(challengeBytes)
                .TrimEnd('=').Replace('+', '-').Replace('/', '_');

            var authorizeUri = new Uri(
                $"https://www.dropbox.com/oauth2/authorize" +
                $"?client_id={Uri.EscapeDataString(appKey)}" +
                $"&response_type=code" +
                $"&redirect_uri={Uri.EscapeDataString(redirectUri)}" +
                $"&state={state}" +
                $"&code_challenge={codeChallenge}" +
                $"&code_challenge_method=S256" +
                $"&token_access_type=offline");

            WriteRegistrationHelp(port);
            Host.UI.WriteLine("Opening browser for Dropbox authorization...");
            Host.UI.WriteLine($"If browser doesn't open, visit: {TerminalHyperlink.Format(authorizeUri.ToString())}");

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
                    $"Choose a different port via -RedirectPort and register http://localhost:<port>/ " +
                    $"in your Dropbox app's redirect URIs. Underlying error: {ex.Message}", ex);
            }

            Host.UI.WriteLine($"Waiting for authorization on {redirectUri} ...");
            Host.UI.WriteLine("(Press Esc or Ctrl+C to cancel.)");

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
                    if (Stopping)
                    {
                        listener.Stop();
                        throw new OperationCanceledException("OAuth flow cancelled by user (Ctrl+C).");
                    }
                    if (TryConsumeEscape())
                    {
                        listener.Stop();
                        throw new OperationCanceledException("OAuth flow cancelled by user (Esc).");
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

            if (!string.IsNullOrEmpty(errorParam))
                throw new Exception($"OAuth flow returned error: {errorParam}");

            if (returnedState != state)
                throw new Exception("OAuth state mismatch. Possible CSRF attack.");

            if (string.IsNullOrEmpty(code))
                throw new Exception("No authorization code received.");

            var tokenResult = DropboxOAuth2Helper.ProcessCodeFlowAsync(
                code, appKey, appSecret, redirectUri, null, codeVerifier).GetAwaiter().GetResult();

            return (tokenResult.AccessToken, tokenResult.RefreshToken);
        }

        private static bool TryConsumeEscape()
        {
            try
            {
                if (Console.IsInputRedirected) return false;
                while (Console.KeyAvailable)
                {
                    var key = Console.ReadKey(intercept: true);
                    if (key.Key == ConsoleKey.Escape) return true;
                }
            }
            catch
            {
                // KeyAvailable throws InvalidOperationException when input is redirected
                // or no console is attached; treat as "no key".
            }
            return false;
        }

        private void WriteRegistrationHelp(int port)
        {
            var redirectUri = $"http://localhost:{port}/";
            Host.UI.WriteLine("--- Dropbox app registration ---");
            Host.UI.WriteLine($"App console:    {TerminalHyperlink.Format("https://www.dropbox.com/developers/apps")}");
            Host.UI.WriteLine($"Create app:     {TerminalHyperlink.Format("https://www.dropbox.com/developers/apps/create")}");
            Host.UI.WriteLine($"Redirect URI:   {redirectUri}   (add under Settings -> OAuth 2 -> Redirect URIs)");
            Host.UI.WriteLine("Scopes:         files.metadata.read, files.metadata.write,");
            Host.UI.WriteLine("                files.content.read,  files.content.write,");
            Host.UI.WriteLine("                sharing.read,        sharing.write,");
            Host.UI.WriteLine("                account_info.read    (Permissions tab; click Submit)");
            Host.UI.WriteLine("--------------------------------");
        }

        /// <summary>
        /// Derives a PSDrive name from an account email's local-part. Falls
        /// back to "&lt;localpart&gt;_&lt;first-domain-label&gt;" if a drive with the
        /// preferred name already exists, then "_2", "_3", ... so concurrent
        /// connections never clobber each other.
        /// </summary>
        private string DeriveDriveName(string email)
        {
            var atIdx = email.IndexOf('@');
            if (atIdx <= 0) return string.Empty;
            var local  = SanitizeDriveName(email.Substring(0, atIdx));
            var domain = email.Substring(atIdx + 1);
            if (string.IsNullOrEmpty(local)) return string.Empty;

            if (!DriveExists(local)) return local;

            var dot = domain.IndexOf('.');
            var firstLabel = SanitizeDriveName(dot > 0 ? domain.Substring(0, dot) : domain);
            var withDomain = string.IsNullOrEmpty(firstLabel) ? local : (local + "_" + firstLabel);
            if (!DriveExists(withDomain)) return withDomain;

            for (var i = 2; i < 100; i++)
            {
                var candidate = withDomain + "_" + i.ToString(CultureInfo.InvariantCulture);
                if (!DriveExists(candidate)) return candidate;
            }
            return withDomain + "_" + Guid.NewGuid().ToString("N").Substring(0, 6);
        }

        private static string SanitizeDriveName(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return string.Empty;
            var sb = new StringBuilder(raw.Length);
            foreach (var c in raw)
            {
                if (char.IsLetterOrDigit(c) || c == '_') sb.Append(c);
            }
            if (sb.Length == 0) return string.Empty;
            if (char.IsDigit(sb[0])) sb.Insert(0, '_');
            return sb.ToString();
        }

        private bool DriveExists(string name)
        {
            try { return SessionState.Drive.Get(name) != null; }
            catch { return false; }
        }
    }

    /// <summary>Disconnects from Dropbox and removes the PSDrive.</summary>
    [Cmdlet(VerbsCommunications.Disconnect, "Dropbox")]
    public class DisconnectDropboxCommand : PSCmdlet
    {
        [Parameter(Position = 0)]
        public string DriveName { get; set; } = "Dbx";

        protected override void ProcessRecord()
        {
            try
            {
                var drive = SessionState.Drive.Get(DriveName);
                if (drive is DropboxDriveInfo dbxDrive)
                {
                    try { dbxDrive.Cache?.Dispose(); } catch { }
                    dbxDrive.Service.Dispose();
                }
                SessionState.Drive.Remove(DriveName, true, "global");
                try
                {
                    SessionState.InvokeCommand.InvokeScript(
                        false,
                        ScriptBlock.Create($"if (Test-Path function:global:{DriveName}:) {{ Remove-Item function:global:{DriveName}: }}"),
                        null);
                }
                catch { /* best effort */ }
                Host.UI.WriteLine($"Disconnected from Dropbox drive '{DriveName}:'.");
            }
            catch (Exception ex)
            {
                WriteError(new ErrorRecord(ex, "DisconnectFailed",
                    ErrorCategory.CloseError, DriveName));
            }
        }
    }
}
