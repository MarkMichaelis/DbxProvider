using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DbxProvider.Services
{
    /// <summary>
    /// Sentinel key used in the v2 accounts dictionary for credentials that
    /// have not yet been associated with a Dropbox account id (e.g. legacy
    /// credentials migrated from v1, or app-key/secret saved before the first
    /// successful auth). Once a successful Connect-Dropbox call yields an
    /// accountId, the entry is re-keyed under that accountId.
    /// </summary>
    internal static class CredentialStoreConstants
    {
        public const string PreAuthKey = "default";
    }

    /// <summary>
    /// Persisted Dropbox credentials for a single Dropbox account. Secret
    /// fields are stored as base64-encoded ciphertext (DPAPI on Windows,
    /// plaintext-base64 elsewhere with a warning).
    /// </summary>
    public sealed class StoredAccount
    {
        [JsonPropertyName("appKey")]
        public string? AppKey { get; set; }

        [JsonPropertyName("appSecret")]
        public string? AppSecret { get; set; }

        [JsonPropertyName("refreshToken")]
        public string? RefreshToken { get; set; }

        /// <summary>Dropbox account id (e.g. "dbid:..."). Null until first successful auth.</summary>
        [JsonPropertyName("accountId")]
        public string? AccountId { get; set; }

        [JsonPropertyName("email")]
        public string? Email { get; set; }

        [JsonPropertyName("displayName")]
        public string? DisplayName { get; set; }

        [JsonPropertyName("savedAt")]
        public DateTimeOffset SavedAt { get; set; }
    }

    /// <summary>
    /// Backwards-compat alias: legacy single-account credentials shape. This
    /// type is preserved so external callers / tests that referenced
    /// <c>StoredCredentials</c> continue to compile. New code should use
    /// <see cref="StoredAccount"/>.
    /// </summary>
    [Obsolete("Use StoredAccount. Kept for backwards compatibility with v1 callers.")]
    public sealed class StoredCredentials
    {
        public string? AppKey { get; set; }
        public string? AppSecret { get; set; }
        public string? RefreshToken { get; set; }
        public DateTimeOffset SavedAt { get; set; }
    }

    /// <summary>
    /// V2 on-disk shape of <c>credentials.json</c>: a versioned envelope
    /// containing a default account selector and a dictionary of accounts
    /// keyed by Dropbox accountId (or by <see cref="CredentialStoreConstants.PreAuthKey"/>
    /// for entries that pre-date a successful auth).
    /// </summary>
    internal sealed class CredentialFileV2
    {
        [JsonPropertyName("version")]
        public int Version { get; set; } = 2;

        [JsonPropertyName("defaultAccountId")]
        public string? DefaultAccountId { get; set; }

        [JsonPropertyName("accounts")]
        public Dictionary<string, StoredAccount> Accounts { get; set; } = new(StringComparer.Ordinal);
    }

    /// <summary>One row in <see cref="CredentialStore.ListAccounts"/>.</summary>
    public sealed class StoredAccountEntry
    {
        public string Key { get; }
        public StoredAccount Account { get; }
        public bool IsDefault { get; }

        public StoredAccountEntry(string key, StoredAccount account, bool isDefault)
        {
            Key = key;
            Account = account;
            IsDefault = isDefault;
        }
    }

    /// <summary>
    /// Per-user encrypted credential store backed by a JSON file under
    /// LocalApplicationData. Supports multiple Dropbox accounts keyed by
    /// accountId, with a default-account selector. On Windows uses DPAPI
    /// (CurrentUser scope) for secret fields; on other platforms a reversible
    /// base64 obfuscation is used and a warning is surfaced via
    /// <see cref="LastSaveWarning"/>.
    /// </summary>
    public static class CredentialStore
    {
        private const string FolderName = "DbxProvider";
        private const string FileName = "credentials.json";
        private static readonly byte[] DpapiEntropy = Encoding.UTF8.GetBytes("DbxProvider:v1");

        public static string CredentialFilePath
        {
            get
            {
                // Honor $env:LOCALAPPDATA when set so tests can redirect the
                // credential file to a sandbox directory. Fall back to the
                // OS-resolved Special Folder for normal use.
                var root = Environment.GetEnvironmentVariable("LOCALAPPDATA");
                if (string.IsNullOrEmpty(root))
                    root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                if (string.IsNullOrEmpty(root))
                    root = Path.GetTempPath();
                return Path.Combine(root, FolderName, FileName);
            }
        }

        public static bool Exists => File.Exists(CredentialFilePath);

        public static string? LastSaveWarning { get; private set; }

        // ──────────────────────────────────────────────────────────────────
        // V2 account-aware API
        // ──────────────────────────────────────────────────────────────────

        /// <summary>
        /// Returns all stored accounts. The list is empty if the file does
        /// not exist or cannot be parsed.
        /// </summary>
        public static IReadOnlyList<StoredAccountEntry> ListAccounts()
        {
            var file = LoadFile();
            if (file == null || file.Accounts.Count == 0)
                return Array.Empty<StoredAccountEntry>();

            var defaultKey = ResolveDefaultKey(file);
            var result = new List<StoredAccountEntry>(file.Accounts.Count);
            foreach (var kvp in file.Accounts)
            {
                var key = kvp.Key;
                var raw = kvp.Value;
                result.Add(new StoredAccountEntry(key, DecryptAccount(raw),
                    string.Equals(key, defaultKey, StringComparison.Ordinal)));
            }
            return result;
        }

        /// <summary>
        /// Loads a single account, optionally selected by accountId, email, or
        /// unambiguous email local-part. When <paramref name="selector"/> is
        /// null/empty the default account is returned.
        /// </summary>
        /// <exception cref="InvalidOperationException">
        /// Thrown when the selector matches multiple accounts ambiguously.
        /// </exception>
        public static StoredAccount? LoadAccount(string? selector = null)
        {
            var entry = ResolveEntry(selector, throwOnAmbiguous: true);
            return entry?.Account;
        }

        /// <summary>
        /// Returns the dictionary key for the resolved account (useful when the
        /// caller needs to rewrite the entry, e.g. re-keying a "default"
        /// pre-auth entry under its newly-discovered accountId).
        /// </summary>
        public static StoredAccountEntry? ResolveEntry(string? selector, bool throwOnAmbiguous = true)
        {
            var file = LoadFile();
            if (file == null || file.Accounts.Count == 0) return null;

            var defaultKey = ResolveDefaultKey(file);

            // Selector null/empty -> default account.
            if (string.IsNullOrEmpty(selector))
            {
                if (defaultKey == null || !file.Accounts.TryGetValue(defaultKey, out var raw)) return null;
                return new StoredAccountEntry(defaultKey, DecryptAccount(raw),
                    isDefault: true);
            }

            // 1. Exact dictionary key match (covers "dbid:..." and the literal "default" pre-auth key).
            if (file.Accounts.TryGetValue(selector!, out var byKey))
            {
                return new StoredAccountEntry(selector!, DecryptAccount(byKey),
                    string.Equals(selector, defaultKey, StringComparison.Ordinal));
            }

            // 2. Exact accountId match across entries (in case the dict key drifted from the stored AccountId).
            var idMatches = file.Accounts
                .Where(kvp => string.Equals(kvp.Value.AccountId, selector, StringComparison.Ordinal))
                .ToList();
            if (idMatches.Count == 1)
            {
                var kvp = idMatches[0];
                return new StoredAccountEntry(kvp.Key, DecryptAccount(kvp.Value),
                    string.Equals(kvp.Key, defaultKey, StringComparison.Ordinal));
            }

            // 3. Exact email match (case-insensitive).
            var emailMatches = file.Accounts
                .Where(kvp => !string.IsNullOrEmpty(kvp.Value.Email) &&
                              string.Equals(kvp.Value.Email, selector, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (emailMatches.Count == 1)
            {
                var kvp = emailMatches[0];
                return new StoredAccountEntry(kvp.Key, DecryptAccount(kvp.Value),
                    string.Equals(kvp.Key, defaultKey, StringComparison.Ordinal));
            }
            if (emailMatches.Count > 1 && throwOnAmbiguous)
            {
                throw new InvalidOperationException(
                    $"Selector '{selector}' matches multiple stored accounts: " +
                    string.Join(", ", emailMatches.Select(m => m.Value.AccountId ?? m.Key)) +
                    ". Use the accountId or full email to disambiguate.");
            }

            // 4. Email local-part (prefix before '@') unambiguous match.
            var prefixMatches = file.Accounts
                .Where(kvp => !string.IsNullOrEmpty(kvp.Value.Email) &&
                              string.Equals(LocalPart(kvp.Value.Email!), selector,
                                  StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (prefixMatches.Count == 1)
            {
                var kvp = prefixMatches[0];
                return new StoredAccountEntry(kvp.Key, DecryptAccount(kvp.Value),
                    string.Equals(kvp.Key, defaultKey, StringComparison.Ordinal));
            }
            if (prefixMatches.Count > 1 && throwOnAmbiguous)
            {
                throw new InvalidOperationException(
                    $"Selector '{selector}' is ambiguous; matches accounts: " +
                    string.Join(", ", prefixMatches.Select(m => m.Value.Email ?? m.Key)) +
                    ". Use the full email or accountId to disambiguate.");
            }

            return null;
        }

        /// <summary>
        /// Saves (or merges into) an account. If <paramref name="updates"/>.AccountId is
        /// non-null the entry is keyed by that accountId; otherwise it's stored under the
        /// pre-auth sentinel key so the next successful Connect-Dropbox can re-key it.
        /// Null fields on <paramref name="updates"/> preserve any existing value.
        /// If <paramref name="setDefault"/> is true (or the file currently has no default),
        /// the saved account becomes the default.
        /// </summary>
        public static void SaveAccount(StoredAccount updates, bool setDefault = false)
        {
            if (updates == null) throw new ArgumentNullException(nameof(updates));

            LastSaveWarning = null;

            var file = LoadFile() ?? new CredentialFileV2();

            // Find existing entry by AccountId if present, otherwise default/pre-auth.
            string targetKey;
            StoredAccount? existing = null;

            if (!string.IsNullOrEmpty(updates.AccountId))
            {
                targetKey = updates.AccountId!;
                file.Accounts.TryGetValue(targetKey, out existing);

                // If a pre-auth "default" entry exists and we just learned its accountId,
                // adopt its values and remove the placeholder.
                if (existing == null &&
                    file.Accounts.TryGetValue(CredentialStoreConstants.PreAuthKey, out var preAuth) &&
                    string.IsNullOrEmpty(preAuth.AccountId))
                {
                    existing = preAuth;
                    file.Accounts.Remove(CredentialStoreConstants.PreAuthKey);
                    if (string.Equals(file.DefaultAccountId, CredentialStoreConstants.PreAuthKey,
                            StringComparison.Ordinal))
                    {
                        file.DefaultAccountId = targetKey;
                    }
                }
            }
            else
            {
                // No accountId yet — store under the pre-auth sentinel.
                targetKey = CredentialStoreConstants.PreAuthKey;
                file.Accounts.TryGetValue(targetKey, out existing);
            }

            var merged = new StoredAccount
            {
                AppKey       = updates.AppKey       ?? existing?.AppKey,
                AppSecret    = EncryptIfPlain(updates.AppSecret) ?? existing?.AppSecret,
                RefreshToken = EncryptIfPlain(updates.RefreshToken) ?? existing?.RefreshToken,
                AccountId    = updates.AccountId    ?? existing?.AccountId,
                Email        = updates.Email        ?? existing?.Email,
                DisplayName  = updates.DisplayName  ?? existing?.DisplayName,
                SavedAt      = DateTimeOffset.UtcNow
            };

            file.Accounts[targetKey] = merged;

            if (setDefault || string.IsNullOrEmpty(file.DefaultAccountId) ||
                !file.Accounts.ContainsKey(file.DefaultAccountId!))
            {
                file.DefaultAccountId = targetKey;
            }

            WriteFile(file);
        }

        /// <summary>
        /// Removes an account by selector. Returns true when an account was
        /// removed. If the removed entry was the default, a remaining account
        /// becomes the new default.
        /// </summary>
        public static bool RemoveAccount(string? selector)
        {
            var file = LoadFile();
            if (file == null || file.Accounts.Count == 0) return false;

            var entry = ResolveEntry(selector, throwOnAmbiguous: true);
            if (entry == null) return false;

            file.Accounts.Remove(entry.Key);

            if (string.Equals(file.DefaultAccountId, entry.Key, StringComparison.Ordinal))
            {
                file.DefaultAccountId = file.Accounts.Keys.FirstOrDefault();
            }

            if (file.Accounts.Count == 0)
            {
                File.Delete(CredentialFilePath);
            }
            else
            {
                WriteFile(file);
            }
            return true;
        }

        /// <summary>Removes all stored accounts (deletes the credential file).</summary>
        public static bool RemoveAllAccounts()
        {
            var path = CredentialFilePath;
            if (!File.Exists(path)) return false;
            File.Delete(path);
            return true;
        }

        public static string? GetDefaultAccountKey()
        {
            var file = LoadFile();
            return file == null ? null : ResolveDefaultKey(file);
        }

        /// <summary>
        /// Sets the default account by selector. Throws if the selector
        /// doesn't resolve to a stored account.
        /// </summary>
        public static void SetDefaultAccount(string selector)
        {
            if (string.IsNullOrEmpty(selector))
                throw new ArgumentException("Selector is required.", nameof(selector));

            var file = LoadFile()
                ?? throw new InvalidOperationException("No credentials are stored.");
            var entry = ResolveEntry(selector, throwOnAmbiguous: true)
                ?? throw new InvalidOperationException(
                    $"No stored account matches selector '{selector}'.");

            file.DefaultAccountId = entry.Key;
            WriteFile(file);
        }

        // ──────────────────────────────────────────────────────────────────
        // V1 legacy shims (kept so external callers / tests continue to work).
        // These operate on the default account.
        // ──────────────────────────────────────────────────────────────────

#pragma warning disable CS0618 // StoredCredentials is intentionally obsolete

        /// <summary>Legacy single-account loader. Returns the default account.</summary>
        [Obsolete("Use LoadAccount(selector) instead.")]
        public static StoredCredentials? Load()
        {
            var account = LoadAccount(null);
            if (account == null) return null;
            return new StoredCredentials
            {
                AppKey = account.AppKey,
                AppSecret = account.AppSecret,
                RefreshToken = account.RefreshToken,
                SavedAt = account.SavedAt
            };
        }

#pragma warning restore CS0618

        /// <summary>Legacy single-account saver. Writes to the default account.</summary>
        [Obsolete("Use SaveAccount(StoredAccount) instead.")]
        public static void Save(string? appKey, string? appSecret, string? refreshToken)
        {
            // Resolve which entry the legacy call should target: default if any,
            // otherwise create a pre-auth entry.
            var defaultEntry = ResolveEntry(null, throwOnAmbiguous: false);
            var updates = new StoredAccount
            {
                AppKey       = appKey,
                AppSecret    = appSecret,
                RefreshToken = refreshToken,
                AccountId    = defaultEntry?.Account.AccountId,
                Email        = defaultEntry?.Account.Email,
                DisplayName  = defaultEntry?.Account.DisplayName
            };
            SaveAccount(updates, setDefault: false);
        }

        /// <summary>Legacy whole-file clear.</summary>
        [Obsolete("Use RemoveAccount(selector) or RemoveAllAccounts().")]
        public static bool Clear() => RemoveAllAccounts();

        // ──────────────────────────────────────────────────────────────────
        // Internal: load/save/migrate
        // ──────────────────────────────────────────────────────────────────

        /// <summary>
        /// Loads the on-disk credential file, transparently migrating legacy
        /// (v1) single-blob shapes to the v2 envelope. Encrypted secret fields
        /// are returned as-is (still ciphertext); decryption happens at the
        /// account-level boundary in <see cref="DecryptAccount"/>.
        /// </summary>
        private static CredentialFileV2? LoadFile()
        {
            var path = CredentialFilePath;
            if (!File.Exists(path)) return null;

            string json;
            try { json = File.ReadAllText(path); }
            catch { return null; }

            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                if (root.ValueKind == JsonValueKind.Object &&
                    root.TryGetProperty("version", out var v) &&
                    v.ValueKind == JsonValueKind.Number &&
                    root.TryGetProperty("accounts", out _))
                {
                    return JsonSerializer.Deserialize<CredentialFileV2>(json) ?? new CredentialFileV2();
                }

                // Legacy v1 shape: { appKey, appSecret, refreshToken, savedAt }.
                var legacy = JsonSerializer.Deserialize<LegacyCredentialFile>(json);
                if (legacy == null) return null;

                var migrated = new CredentialFileV2
                {
                    Version = 2,
                    DefaultAccountId = CredentialStoreConstants.PreAuthKey
                };
                migrated.Accounts[CredentialStoreConstants.PreAuthKey] = new StoredAccount
                {
                    AppKey       = legacy.AppKey,
                    AppSecret    = legacy.AppSecret,    // already ciphertext, preserve
                    RefreshToken = legacy.RefreshToken, // already ciphertext, preserve
                    AccountId    = null,
                    Email        = null,
                    DisplayName  = null,
                    SavedAt      = legacy.SavedAt
                };
                return migrated;
            }
            catch
            {
                return null;
            }
        }

        private static void WriteFile(CredentialFileV2 file)
        {
            var path = CredentialFilePath;
            var dir = Path.GetDirectoryName(path)!;
            Directory.CreateDirectory(dir);

            var json = JsonSerializer.Serialize(file, new JsonSerializerOptions
            {
                WriteIndented = true,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
            });
            File.WriteAllText(path, json);
            TryRestrictPermissions(path);
        }

        private static StoredAccount DecryptAccount(StoredAccount stored) => new()
        {
            AppKey       = stored.AppKey,
            AppSecret    = Decrypt(stored.AppSecret),
            RefreshToken = Decrypt(stored.RefreshToken),
            AccountId    = stored.AccountId,
            Email        = stored.Email,
            DisplayName  = stored.DisplayName,
            SavedAt      = stored.SavedAt
        };

        private static string? ResolveDefaultKey(CredentialFileV2 file)
        {
            if (!string.IsNullOrEmpty(file.DefaultAccountId) &&
                file.Accounts.ContainsKey(file.DefaultAccountId!))
            {
                return file.DefaultAccountId;
            }
            return file.Accounts.Keys.FirstOrDefault();
        }

        private static string LocalPart(string email)
        {
            var at = email.IndexOf('@');
            return at <= 0 ? email : email.Substring(0, at);
        }

        /// <summary>
        /// Encrypts a plaintext value if it isn't already a stored ciphertext
        /// (with "dpapi:" or "plain:" prefix). Returns null when the input is
        /// null/empty so the caller can fall back to the existing stored value.
        /// </summary>
        private static string? EncryptIfPlain(string? value)
        {
            if (value == null) return null;
            if (value.Length == 0) return null;
            if (value.StartsWith("dpapi:", StringComparison.Ordinal) ||
                value.StartsWith("plain:", StringComparison.Ordinal))
            {
                return value;
            }
            return Encrypt(value);
        }

        /// <summary>Internal v1 shape, used only by the migration path.</summary>
        private sealed class LegacyCredentialFile
        {
            [JsonPropertyName("appKey")]        public string? AppKey { get; set; }
            [JsonPropertyName("appSecret")]     public string? AppSecret { get; set; }
            [JsonPropertyName("refreshToken")]  public string? RefreshToken { get; set; }
            [JsonPropertyName("savedAt")]       public DateTimeOffset SavedAt { get; set; }
        }


        private static string? Encrypt(string? plaintext)
        {
            if (string.IsNullOrEmpty(plaintext)) return null;
            var bytes = Encoding.UTF8.GetBytes(plaintext);

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                try
                {
                    var ciphertext = ProtectedData.Protect(bytes, DpapiEntropy, DataProtectionScope.CurrentUser);
                    return "dpapi:" + Convert.ToBase64String(ciphertext);
                }
                catch (Exception ex)
                {
                    LastSaveWarning = $"DPAPI encryption failed ({ex.Message}); secrets stored without encryption.";
                }
            }
            else
            {
                LastSaveWarning = "Secrets are stored without OS-level encryption on this platform. Restrict file permissions on " + CredentialFilePath + ".";
            }

            return "plain:" + Convert.ToBase64String(bytes);
        }

        private static string? Decrypt(string? stored)
        {
            if (string.IsNullOrEmpty(stored)) return null;

            if (stored!.StartsWith("dpapi:", StringComparison.Ordinal))
            {
                if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                    return null;
                var cipher = Convert.FromBase64String(stored.Substring(6));
                var plain = ProtectedData.Unprotect(cipher, DpapiEntropy, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(plain);
            }

            if (stored.StartsWith("plain:", StringComparison.Ordinal))
            {
                var bytes = Convert.FromBase64String(stored.Substring(6));
                return Encoding.UTF8.GetString(bytes);
            }

            // Legacy / unknown — return as-is.
            return stored;
        }

        private static void TryRestrictPermissions(string path)
        {
            try
            {
                if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
#if NET8_0_OR_GREATER
                    File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
#endif
                }
            }
            catch
            {
                // Best-effort; ignore.
            }
        }
    }
}
