using System;
using System.Management.Automation;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using Dropbox.Api;
using DbxProvider.Provider;
using DbxProvider.Services;

namespace DbxProvider.Cmdlets
{
    /// <summary>Authenticates to Dropbox and creates a PSDrive.</summary>
    [Cmdlet(VerbsCommunications.Connect, "Dropbox", DefaultParameterSetName = "Token")]
    [OutputType(typeof(PSDriveInfo))]
    public class ConnectDropboxCommand : PSCmdlet
    {
        [Parameter(Mandatory = true, ParameterSetName = "Token", Position = 0)]
        public string AccessToken { get; set; } = string.Empty;

        [Parameter(Mandatory = true, ParameterSetName = "OAuth")]
        public string AppKey { get; set; } = string.Empty;

        [Parameter(ParameterSetName = "OAuth")]
        public string? AppSecret { get; set; }

        [Parameter]
        public string DriveName { get; set; } = "Dbx";

        [Parameter(ParameterSetName = "RefreshToken")]
        [Parameter(ParameterSetName = "OAuth")]
        public string? RefreshToken { get; set; }

        protected override void ProcessRecord()
        {
            try
            {
                string token;

                if (ParameterSetName == "OAuth" && string.IsNullOrEmpty(RefreshToken))
                {
                    token = RunOAuthFlow();
                }
                else if (ParameterSetName == "RefreshToken")
                {
                    token = AccessToken;
                }
                else
                {
                    token = AccessToken;
                }

                // Verify the token works
                var service = new DropboxServiceClient(token);
                var account = service.GetCurrentAccountAsync().GetAwaiter().GetResult();
                WriteVerbose($"Authenticated as {account.DisplayName} ({account.Email})");

                // Create the PSDrive
                var driveInfo = new PSDriveInfo(
                    DriveName, SessionState.Provider.GetOne("Dropbox"),
                    "\\", $"Dropbox ({account.Email})", null);

                var dbxDrive = new DropboxDriveInfo(driveInfo, token);
                SessionState.Drive.New(dbxDrive, "global");
                WriteObject(dbxDrive);
                Host.UI.WriteLine($"Connected to Dropbox as {account.DisplayName}. Use '{DriveName}:' to navigate.");
            }
            catch (Exception ex)
            {
                ThrowTerminatingError(new ErrorRecord(ex, "ConnectDropboxFailed",
                    ErrorCategory.AuthenticationError, null));
            }
        }

        private string RunOAuthFlow()
        {
            var redirectUri = "http://localhost:52475/";
            var state = Guid.NewGuid().ToString("N");

            // Build PKCE challenge
            var codeVerifier = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
            using var sha256 = System.Security.Cryptography.SHA256.Create();
            var challengeBytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(codeVerifier));
            var codeChallenge = Convert.ToBase64String(challengeBytes)
                .TrimEnd('=').Replace('+', '-').Replace('/', '_');

            var authorizeUri = new Uri(
                $"https://www.dropbox.com/oauth2/authorize" +
                $"?client_id={Uri.EscapeDataString(AppKey)}" +
                $"&response_type=code" +
                $"&redirect_uri={Uri.EscapeDataString(redirectUri)}" +
                $"&state={state}" +
                $"&code_challenge={codeChallenge}" +
                $"&code_challenge_method=S256" +
                $"&token_access_type=offline");

            Host.UI.WriteLine("Opening browser for Dropbox authorization...");
            Host.UI.WriteLine($"If browser doesn't open, visit: {authorizeUri}");

            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo(authorizeUri.ToString())
                { UseShellExecute = true };
                System.Diagnostics.Process.Start(psi);
            }
            catch { /* Browser may not open in some environments */ }

            // Start a temporary HTTP listener to receive the callback
            using var listener = new HttpListener();
            listener.Prefixes.Add(redirectUri);
            listener.Start();

            Host.UI.WriteLine("Waiting for authorization...");
            var context = listener.GetContext();
            var code = context.Request.QueryString["code"];
            var returnedState = context.Request.QueryString["state"];

            // Send response to browser
            var responseHtml = "<html><body><h2>Authorization successful!</h2><p>You can close this window.</p></body></html>";
            var buffer = Encoding.UTF8.GetBytes(responseHtml);
            context.Response.ContentLength64 = buffer.Length;
            context.Response.OutputStream.Write(buffer, 0, buffer.Length);
            context.Response.Close();
            listener.Stop();

            if (returnedState != state)
                throw new Exception("OAuth state mismatch. Possible CSRF attack.");

            if (string.IsNullOrEmpty(code))
                throw new Exception("No authorization code received.");

            // Exchange code for token using PKCE flow
            var tokenResult = DropboxOAuth2Helper.ProcessCodeFlowAsync(
                code, AppKey, AppSecret, redirectUri, null, codeVerifier).GetAwaiter().GetResult();

            if (!string.IsNullOrEmpty(tokenResult.RefreshToken))
            {
                Host.UI.WriteLine($"Refresh token (save for future use): {tokenResult.RefreshToken}");
            }

            return tokenResult.AccessToken;
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
                    dbxDrive.Service.Dispose();
                }
                SessionState.Drive.Remove(DriveName, true, "global");
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