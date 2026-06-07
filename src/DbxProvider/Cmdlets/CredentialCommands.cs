using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Management.Automation;
using System.Management.Automation.Language;
using IntelliTect.Dropbox;

namespace DbxProvider.Cmdlets
{
    /// <summary>
    /// Argument completer for <c>-Account</c> parameters. Returns saved
    /// accountIds and email addresses from the credential store.
    /// </summary>
    public sealed class AccountSelectorCompleter : IArgumentCompleter
    {
        public IEnumerable<CompletionResult> CompleteArgument(
            string commandName, string parameterName, string wordToComplete,
            CommandAst commandAst, IDictionary fakeBoundParameters)
        {
            IReadOnlyList<StoredAccountEntry> accounts;
            try { accounts = CredentialStore.ListAccounts(); }
            catch { yield break; }

            wordToComplete ??= string.Empty;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var entry in accounts)
            {
                foreach (var candidate in new[] { entry.Account.Email, entry.Account.AccountId, entry.Key })
                {
                    if (string.IsNullOrEmpty(candidate)) continue;
                    if (!seen.Add(candidate)) continue;
                    if (wordToComplete.Length == 0 ||
                        candidate.StartsWith(wordToComplete, StringComparison.OrdinalIgnoreCase))
                    {
                        var quoted = candidate.Contains(' ') ? "'" + candidate + "'" : candidate;
                        var tooltip = entry.Account.Email != null
                            ? $"{entry.Account.Email} ({entry.Account.AccountId ?? entry.Key})" +
                              (entry.IsDefault ? " [default]" : "")
                            : entry.Key;
                        yield return new CompletionResult(quoted, candidate, CompletionResultType.ParameterValue, tooltip);
                    }
                }
            }
        }
    }

    /// <summary>Returns Dropbox credentials saved in the per-user credential store.</summary>
    [Cmdlet(VerbsCommon.Get, "DropboxCredential", DefaultParameterSetName = SingleSet)]
    [OutputType(typeof(PSObject))]
    public class GetDropboxCredentialCommand : PSCmdlet
    {
        private const string SingleSet = "Single";
        private const string AllSet = "All";

        /// <summary>Reveal the AppSecret and RefreshToken in plain text.</summary>
        [Parameter]
        public SwitchParameter AsPlainText { get; set; }

        /// <summary>
        /// Select an account by Dropbox accountId, email, or unambiguous email
        /// local-part. When omitted, the default account is returned.
        /// </summary>
        [Parameter(ParameterSetName = SingleSet, Position = 0)]
        [ArgumentCompleter(typeof(AccountSelectorCompleter))]
        public string? Account { get; set; }

        /// <summary>List every saved account.</summary>
        [Parameter(Mandatory = true, ParameterSetName = AllSet)]
        public SwitchParameter All { get; set; }

        protected override void ProcessRecord()
        {
            if (ParameterSetName == AllSet)
            {
                var entries = CredentialStore.ListAccounts();
                if (entries.Count == 0)
                {
                    WriteVerbose($"No saved credentials at {CredentialStore.CredentialFilePath}.");
                    return;
                }
                foreach (var entry in entries) WriteObject(ToPSObject(entry));
                return;
            }

            StoredAccountEntry? single;
            try { single = CredentialStore.ResolveEntry(Account, throwOnAmbiguous: true); }
            catch (InvalidOperationException ex)
            {
                WriteError(new ErrorRecord(ex, "AmbiguousAccount",
                    ErrorCategory.InvalidArgument, Account));
                return;
            }

            if (single == null)
            {
                WriteVerbose($"No saved credentials at {CredentialStore.CredentialFilePath}.");
                return;
            }

            WriteObject(ToPSObject(single));
        }

        private PSObject ToPSObject(StoredAccountEntry entry)
        {
            var output = new PSObject();
            output.Properties.Add(new PSNoteProperty("AccountId", entry.Account.AccountId));
            output.Properties.Add(new PSNoteProperty("Email", entry.Account.Email));
            output.Properties.Add(new PSNoteProperty("DisplayName", entry.Account.DisplayName));
            output.Properties.Add(new PSNoteProperty("IsDefault", entry.IsDefault));
            output.Properties.Add(new PSNoteProperty("AppKey", entry.Account.AppKey));
            output.Properties.Add(new PSNoteProperty("AppSecret",
                AsPlainText ? entry.Account.AppSecret : Mask(entry.Account.AppSecret)));
            output.Properties.Add(new PSNoteProperty("RefreshToken",
                AsPlainText ? entry.Account.RefreshToken : Mask(entry.Account.RefreshToken)));
            output.Properties.Add(new PSNoteProperty("SavedAt", entry.Account.SavedAt));
            output.Properties.Add(new PSNoteProperty("Path", CredentialStore.CredentialFilePath));
            return output;
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

        /// <summary>
        /// Account selector (accountId, email, or unambiguous email local-part).
        /// When omitted, updates the default account (or creates a pre-auth
        /// entry under the default key).
        /// </summary>
        [Parameter]
        [ArgumentCompleter(typeof(AccountSelectorCompleter))]
        public string? Account { get; set; }

        /// <summary>Mark this account as the default after saving.</summary>
        [Parameter]
        public SwitchParameter SetDefault { get; set; }

        protected override void ProcessRecord()
        {
            // Resolve which entry the legacy/positional save targets so we
            // can preserve account metadata (Email/AccountId/DisplayName).
            StoredAccountEntry? existing = null;
            try { existing = CredentialStore.ResolveEntry(Account, throwOnAmbiguous: true); }
            catch (InvalidOperationException ex)
            {
                WriteError(new ErrorRecord(ex, "AmbiguousAccount",
                    ErrorCategory.InvalidArgument, Account));
                return;
            }

            // If a selector was supplied but didn't resolve, treat it as the
            // accountId of a fresh entry (lets users pre-create a stub).
            string? targetAccountId = existing?.Account.AccountId;
            string? targetEmail = existing?.Account.Email;
            string? targetDisplay = existing?.Account.DisplayName;
            if (existing == null && !string.IsNullOrEmpty(Account))
            {
                if (Account!.StartsWith("dbid:", StringComparison.Ordinal))
                    targetAccountId = Account;
                else if (Account.Contains('@'))
                    targetEmail = Account;
                else
                {
                    WriteError(new ErrorRecord(
                        new InvalidOperationException(
                            $"No saved account matches '{Account}'. " +
                            "Pass an accountId (dbid:...) or full email to create a new entry."),
                        "AccountNotFound", ErrorCategory.ObjectNotFound, Account));
                    return;
                }
            }

            CredentialStore.SaveAccount(new StoredAccount
            {
                AppKey = AppKey,
                AppSecret = AppSecret,
                RefreshToken = RefreshToken,
                AccountId = targetAccountId,
                Email = targetEmail,
                DisplayName = targetDisplay
            }, setDefault: SetDefault.IsPresent);

            if (!string.IsNullOrEmpty(CredentialStore.LastSaveWarning))
                WriteWarning(CredentialStore.LastSaveWarning);
            WriteVerbose($"Credentials saved to {CredentialStore.CredentialFilePath}.");
        }
    }

    /// <summary>Removes saved Dropbox credentials.</summary>
    [Cmdlet(VerbsCommon.Remove, "DropboxCredential", SupportsShouldProcess = true,
            DefaultParameterSetName = SingleSet)]
    public class RemoveDropboxCredentialCommand : PSCmdlet
    {
        private const string SingleSet = "Single";
        private const string AllSet = "All";

        /// <summary>
        /// Account selector to remove. When omitted (and -All is not specified),
        /// the default account is removed.
        /// </summary>
        [Parameter(ParameterSetName = SingleSet, Position = 0)]
        [ArgumentCompleter(typeof(AccountSelectorCompleter))]
        public string? Account { get; set; }

        /// <summary>Remove every saved account (deletes the credential file).</summary>
        [Parameter(Mandatory = true, ParameterSetName = AllSet)]
        public SwitchParameter All { get; set; }

        protected override void ProcessRecord()
        {
            var path = CredentialStore.CredentialFilePath;
            if (!CredentialStore.Exists)
            {
                WriteVerbose($"No saved credentials at {path}.");
                return;
            }

            if (ParameterSetName == AllSet)
            {
                if (ShouldProcess(path, "Remove all saved Dropbox credentials"))
                {
                    CredentialStore.RemoveAllAccounts();
                    Host.UI.WriteLine($"Removed all credentials at {path}.");
                }
                return;
            }

            StoredAccountEntry? entry;
            try { entry = CredentialStore.ResolveEntry(Account, throwOnAmbiguous: true); }
            catch (InvalidOperationException ex)
            {
                WriteError(new ErrorRecord(ex, "AmbiguousAccount",
                    ErrorCategory.InvalidArgument, Account));
                return;
            }

            if (entry == null)
            {
                WriteVerbose(string.IsNullOrEmpty(Account)
                    ? $"No default account in {path}."
                    : $"No saved account matches '{Account}'.");
                return;
            }

            var label = entry.Account.Email
                        ?? entry.Account.AccountId
                        ?? entry.Key;
            if (ShouldProcess($"{label} ({path})", "Remove saved Dropbox credentials"))
            {
                CredentialStore.RemoveAccount(entry.Key);
                Host.UI.WriteLine($"Removed credentials for {label}.");
            }
        }
    }
}

