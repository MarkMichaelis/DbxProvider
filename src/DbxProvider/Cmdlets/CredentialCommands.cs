using System;
using System.Management.Automation;
using DbxProvider.Services;

namespace DbxProvider.Cmdlets
{
    /// <summary>Returns the currently saved Dropbox credentials.</summary>
    [Cmdlet(VerbsCommon.Get, "DropboxCredential")]
    [OutputType(typeof(PSObject))]
    public class GetDropboxCredentialCommand : PSCmdlet
    {
        /// <summary>Reveal the AppSecret and RefreshToken in plain text.</summary>
        [Parameter]
        public SwitchParameter AsPlainText { get; set; }

        protected override void ProcessRecord()
        {
            var stored = CredentialStore.Load();
            if (stored == null)
            {
                WriteVerbose($"No saved credentials at {CredentialStore.CredentialFilePath}.");
                return;
            }

            var output = new PSObject();
            output.Properties.Add(new PSNoteProperty("AppKey", stored.AppKey));
            output.Properties.Add(new PSNoteProperty("AppSecret",
                AsPlainText ? stored.AppSecret : Mask(stored.AppSecret)));
            output.Properties.Add(new PSNoteProperty("RefreshToken",
                AsPlainText ? stored.RefreshToken : Mask(stored.RefreshToken)));
            output.Properties.Add(new PSNoteProperty("SavedAt", stored.SavedAt));
            output.Properties.Add(new PSNoteProperty("Path", CredentialStore.CredentialFilePath));
            WriteObject(output);
        }

        private static string? Mask(string? value)
        {
            if (string.IsNullOrEmpty(value)) return null;
            if (value.Length <= 4) return new string('*', value.Length);
            return new string('*', Math.Max(value.Length - 4, 4)) + value.Substring(value.Length - 4);
        }
    }

    /// <summary>Saves Dropbox credentials to the per-user credential store.</summary>
    [Cmdlet(VerbsCommon.Set, "DropboxCredential")]
    public class SetDropboxCredentialCommand : PSCmdlet
    {
        [Parameter]
        public string? AppKey { get; set; }

        [Parameter]
        public string? AppSecret { get; set; }

        [Parameter]
        public string? RefreshToken { get; set; }

        protected override void ProcessRecord()
        {
            CredentialStore.Save(AppKey, AppSecret, RefreshToken);
            if (!string.IsNullOrEmpty(CredentialStore.LastSaveWarning))
                WriteWarning(CredentialStore.LastSaveWarning);
            WriteVerbose($"Credentials saved to {CredentialStore.CredentialFilePath}.");
        }
    }

    /// <summary>Removes saved Dropbox credentials.</summary>
    [Cmdlet(VerbsCommon.Remove, "DropboxCredential", SupportsShouldProcess = true)]
    public class RemoveDropboxCredentialCommand : PSCmdlet
    {
        protected override void ProcessRecord()
        {
            var path = CredentialStore.CredentialFilePath;
            if (!CredentialStore.Exists)
            {
                WriteVerbose($"No saved credentials at {path}.");
                return;
            }
            if (ShouldProcess(path, "Remove saved Dropbox credentials"))
            {
                CredentialStore.Clear();
                Host.UI.WriteLine($"Removed credentials at {path}.");
            }
        }
    }
}
