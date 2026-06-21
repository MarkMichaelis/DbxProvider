using System;
using System.Globalization;
using System.Management.Automation;
using System.Text;
using DbxProvider.Provider;
using DbxProvider.Services;
using IntelliTect.Dropbox;
using IntelliTect.Dropbox.Auth;

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

        /// <summary>
        /// Cancelled when the pipeline stops (Ctrl+C) via <see cref="StopProcessing"/>.
        /// Threaded into the loopback OAuth flow and the Playwright app-registration
        /// wizard so both blocking operations unblock promptly on cancellation.
        /// </summary>
        private System.Threading.CancellationTokenSource? _stopCts;

        /// <summary>
        /// Activity id for the transient "Connecting to Dropbox..." progress record.
        /// </summary>
        private const int ConnectProgressActivityId = 1;

        /// <summary>
        /// Invoked by PowerShell on a separate thread when the pipeline is stopped
        /// (Ctrl+C). Cancels <see cref="_stopCts"/> so the OAuth callback wait and the
        /// app-registration wizard terminate instead of hanging.
        /// </summary>
        protected override void StopProcessing()
        {
            try { _stopCts?.Cancel(); }
            catch (ObjectDisposedException) { /* already torn down */ }
            base.StopProcessing();
        }

        protected override void ProcessRecord()
        {
            _stopCts = new System.Threading.CancellationTokenSource();
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

                    // No matching saved account and no -AppKey on the command line
                    // means this is a brand-new setup (or a new account on a Dropbox
                    // app still in Development mode, where each Dropbox user needs
                    // their own app). Walk the user through registering a Dropbox
                    // app: open the create-app page, show the values to paste, and
                    // prompt for the resulting AppKey so they only have to click
                    // through the browser steps.
                    if (saved == null
                        && string.IsNullOrEmpty(appKey)
                        && !MyInvocation.BoundParameters.ContainsKey(nameof(AppKey)))
                    {
                        var registered = PromptForNewAppRegistration(RedirectPort);
                        if (registered != null)
                        {
                            appKey    = registered.Value.AppKey;
                            appSecret = registered.Value.AppSecret;
                        }
                    }

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
                        // PromptForNewAppRegistration was non-interactive or returned null:
                        // emit the clear actionable message.
                        WriteRegistrationHelp(RedirectPort);
                        throw new InvalidOperationException(
                            "No credentials available. Provide -AccessToken, or -AppKey (with optional -AppSecret), " +
                            "or run Set-DropboxCredential to populate the credential store first.");
                    }
                }

                // Transient "Connecting to Dropbox..." status. WriteProgress auto-clears
                // when completed, so the line disappears once we are connected, leaving
                // only the "Connected to Dropbox as ..." confirmation below.
                var connectProgress = new ProgressRecord(
                    ConnectProgressActivityId, "Connecting to Dropbox", "Authenticating...");
                WriteProgress(connectProgress);

                var account = service.GetCurrentAccountAsync().GetAwaiter().GetResult();
                WriteVerbose($"Authenticated as {account.DisplayName} ({account.Email})");

                connectProgress.RecordType = ProgressRecordType.Completed;
                WriteProgress(connectProgress);

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
                dbxDrive.InitializeCache(account.AccountId, account.Email);
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
                // Clear any lingering "Connecting to Dropbox..." progress so it does not
                // remain on screen after a failed connect.
                WriteProgress(new ProgressRecord(ConnectProgressActivityId, "Connecting to Dropbox", "Failed.")
                {
                    RecordType = ProgressRecordType.Completed
                });
                ThrowTerminatingError(new ErrorRecord(ex, "ConnectDropboxFailed",
                    ErrorCategory.AuthenticationError, null));
            }
            finally
            {
                _stopCts?.Dispose();
                _stopCts = null;
            }
        }

        private (string AccessToken, string? RefreshToken) RunOAuthFlow(string appKey, string? appSecret, int port)
        {
            WriteRegistrationHelp(port);

            // Cancel the loopback flow when the pipeline stops (Ctrl+C) or the
            // user presses Esc, preserving the prior cancellation behaviour.
            // Linking to _stopCts means StopProcessing() (Ctrl+C) also fires here.
            using var cts = _stopCts != null
                ? System.Threading.CancellationTokenSource.CreateLinkedTokenSource(_stopCts.Token)
                : new System.Threading.CancellationTokenSource();
            var monitor = System.Threading.Tasks.Task.Run(() =>
            {
                while (!cts.IsCancellationRequested)
                {
                    if (Stopping || TryConsumeEscape())
                    {
                        cts.Cancel();
                        break;
                    }
                    System.Threading.Thread.Sleep(150);
                }
            });

            try
            {
                var flow = new LoopbackOAuthFlow(new CmdletConsole(Host.UI));
                var cred = flow.RunAsync(appKey, appSecret, port, cts.Token)
                    .GetAwaiter().GetResult();
                return (cred.AccessToken ?? string.Empty, cred.RefreshToken);
            }
            finally
            {
                cts.Cancel();
                try { monitor.Wait(TimeSpan.FromSeconds(1)); } catch { /* best-effort */ }
            }
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
        /// Walks a brand-new user through registering a Dropbox app: prints the
        /// values they need to paste, opens the create-app page in their default
        /// browser, then prompts for the resulting AppKey (and optional
        /// AppSecret). Returns <c>null</c> when the host is non-interactive
        /// (callers fall back to a clear error message in that case).
        ///
        /// <para>
        /// This is the default first-run UX. Sharing one Dropbox app's AppKey
        /// across multiple accounts only works once the app is in Production
        /// status; while it is still in Development, every Dropbox user that
        /// is not the app owner needs their own app, which is exactly what
        /// this wizard automates.
        /// </para>
        /// </summary>
        private NewAppRegistration? PromptForNewAppRegistration(int port)
        {
            if (!IsInteractiveHost())
            {
                return null;
            }

            var redirectUri = $"http://localhost:{port}/";
            var scopes = new[]
            {
                "files.metadata.read", "files.metadata.write",
                "files.content.read",  "files.content.write",
                "sharing.read",        "sharing.write",
                "account_info.read",
            };

            // ---- Try the Playwright-driven auto-registrar first. ----
            var browser = DefaultBrowser.Detect();
            if (browser.IsChromiumFamily && !string.IsNullOrEmpty(browser.ExecutablePath))
            {
                Host.UI.WriteLine();
                Host.UI.WriteLine($"Detected default browser: {browser.FriendlyName}.");
                Host.UI.WriteLine("Pre-filling the Dropbox app-creation form for you. Sign in if prompted,");
                Host.UI.WriteLine("review the pre-filled fields, then click 'Create app' in the browser.");
                Host.UI.WriteLine("(If anything goes wrong we'll fall back to a manual wizard.)");
                Host.UI.WriteLine();

                try
                {
                    var launcher = new PlaywrightBrowserLauncher(browser.ExecutablePath!);
                    try
                    {
                        var registrar = new DropboxAppRegistrar(
                            launcher,
                            new CmdletConsole(Host.UI));
                        var result = registrar
                            .RegisterAsync(redirectUri, scopes, _stopCts?.Token ?? System.Threading.CancellationToken.None)
                            .GetAwaiter().GetResult();
                        if (result is not null)
                        {
                            Host.UI.WriteLine($"App '{result.AppName}' registered. App key captured.");
                            return new NewAppRegistration(result.AppKey, result.AppSecret);
                        }
                        Host.UI.WriteLine("Auto-registration did not complete. Falling back to manual wizard.");
                    }
                    finally
                    {
                        launcher.DisposeAsync().AsTask().GetAwaiter().GetResult();
                    }
                }
                catch (Exception ex)
                {
                    WriteVerbose($"Auto-registrar threw: {ex.Message}. Falling back to manual wizard.");
                }
            }
            else
            {
                WriteVerbose($"Default browser is '{browser.FriendlyName}' (not Chromium-family); using manual wizard.");
            }

            return PromptForNewAppRegistrationManual(port, redirectUri);
        }

        private NewAppRegistration? PromptForNewAppRegistrationManual(int port, string redirectUri)
        {
            var createUrl = "https://www.dropbox.com/developers/apps/create";

            Host.UI.WriteLine();
            Host.UI.WriteLine("No saved Dropbox credentials match this request.");
            Host.UI.WriteLine("Let's register a Dropbox app for this account. The browser will open the");
            Host.UI.WriteLine("Dropbox app-creation page. Dropbox has no API to create apps for you, so the");
            Host.UI.WriteLine("steps below are manual — but they take under a minute.");
            Host.UI.WriteLine();
            Host.UI.WriteLine("--- Step 1: on the 'Create a new app' page ---");
            Host.UI.WriteLine("  1. Choose an API:           Scoped access");
            Host.UI.WriteLine("  2. Type of access:          Full Dropbox  (or App folder, your choice)");
            Host.UI.WriteLine("  3. Name your app:           anything globally unique");
            Host.UI.WriteLine("                              (e.g. dbxprovider-<your-initials>-<random>)");
            Host.UI.WriteLine("     Click [Create app].");
            Host.UI.WriteLine();
            Host.UI.WriteLine("--- Step 2: on the new app's Settings tab ---");
            Host.UI.WriteLine($"  4. OAuth 2 -> Redirect URIs -> add:  {redirectUri}");
            Host.UI.WriteLine("     (then click Add)");
            Host.UI.WriteLine();
            Host.UI.WriteLine("--- Step 3: switch to the Permissions tab ---");
            Host.UI.WriteLine("  5. Check these scopes, then click [Submit] at the bottom:");
            Host.UI.WriteLine("        files.metadata.read   files.metadata.write");
            Host.UI.WriteLine("        files.content.read    files.content.write");
            Host.UI.WriteLine("        sharing.read          sharing.write");
            Host.UI.WriteLine("        account_info.read");
            Host.UI.WriteLine();
            Host.UI.WriteLine("--- Step 4: back on the Settings tab ---");
            Host.UI.WriteLine("  6. Copy the 'App key' value and paste it below.");
            Host.UI.WriteLine("------------------------------------------------------");
            Host.UI.WriteLine();
            Host.UI.WriteLine($"Opening {TerminalHyperlink.Format(createUrl)} ...");
            Host.UI.WriteLine("(If the browser does not open, copy the link above.)");

            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo(createUrl) { UseShellExecute = true };
                System.Diagnostics.Process.Start(psi);
            }
            catch
            {
                // Browser launch is best-effort.
            }

            Host.UI.WriteLine();
            Host.UI.Write("Paste the App key here: ");
            string? typedKey;
            while (true)
            {
                typedKey = Host.UI.ReadLine()?.Trim();
                if (typedKey == null)
                {
                    return null; // host closed input
                }
                if (typedKey.Length == 0)
                {
                    throw new OperationCanceledException("App registration cancelled (no AppKey provided).");
                }
                // Dropbox app keys are 15 alphanumeric characters; accept anything
                // 10+ chars to be forgiving of future format changes but reject
                // obvious paste mistakes (URLs, whitespace).
                if (typedKey.Length >= 10 && !typedKey.Contains(' ') && !typedKey.Contains('/'))
                {
                    break;
                }
                Host.UI.WriteLine("That doesn't look like an App key. Paste the value from the Settings tab (it's a short alphanumeric string).");
                Host.UI.Write("App key: ");
            }

            Host.UI.Write("App secret (press Enter to skip; only required for confidential apps): ");
            var typedSecret = Host.UI.ReadLine()?.Trim();
            if (string.IsNullOrEmpty(typedSecret))
            {
                typedSecret = null;
            }

            return new NewAppRegistration(typedKey!, typedSecret);
        }

        private bool IsInteractiveHost()
        {
            try
            {
                // Host.UI.ReadLine throws on hosts without an interactive console
                // (e.g. when stdin is redirected). RawUI.KeyAvailable being
                // unsupported is a reliable proxy for "no terminal".
                _ = Host.UI.RawUI.KeyAvailable;
                return !Console.IsInputRedirected;
            }
            catch
            {
                return false;
            }
        }

        private readonly struct NewAppRegistration
        {
            public NewAppRegistration(string appKey, string? appSecret)
            {
                AppKey = appKey;
                AppSecret = appSecret;
            }
            public string AppKey { get; }
            public string? AppSecret { get; }
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
