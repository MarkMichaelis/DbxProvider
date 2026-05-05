using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DbxProvider.Services
{
    /// <summary>
    /// Persisted Dropbox credentials. Secret fields are stored as base64-encoded
    /// ciphertext (DPAPI on Windows, plaintext-base64 elsewhere with a warning).
    /// </summary>
    public sealed class StoredCredentials
    {
        [JsonPropertyName("appKey")]
        public string? AppKey { get; set; }

        [JsonPropertyName("appSecret")]
        public string? AppSecret { get; set; }

        [JsonPropertyName("refreshToken")]
        public string? RefreshToken { get; set; }

        [JsonPropertyName("savedAt")]
        public DateTimeOffset SavedAt { get; set; }
    }

    /// <summary>
    /// Per-user encrypted credential store backed by a JSON file under LocalApplicationData.
    /// On Windows uses DPAPI (CurrentUser scope) for secret fields; on other platforms a
    /// reversible base64 obfuscation is used and a warning is surfaced via <see cref="LastSaveWarning"/>.
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
                var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                if (string.IsNullOrEmpty(root))
                    root = Path.GetTempPath();
                return Path.Combine(root, FolderName, FileName);
            }
        }

        public static bool Exists => File.Exists(CredentialFilePath);

        public static string? LastSaveWarning { get; private set; }

        public static StoredCredentials? Load()
        {
            var path = CredentialFilePath;
            if (!File.Exists(path)) return null;

            try
            {
                var json = File.ReadAllText(path);
                var raw = JsonSerializer.Deserialize<StoredCredentials>(json);
                if (raw == null) return null;

                return new StoredCredentials
                {
                    AppKey = raw.AppKey,
                    AppSecret = Decrypt(raw.AppSecret),
                    RefreshToken = Decrypt(raw.RefreshToken),
                    SavedAt = raw.SavedAt
                };
            }
            catch
            {
                return null;
            }
        }

        public static void Save(string? appKey, string? appSecret, string? refreshToken)
        {
            LastSaveWarning = null;

            var path = CredentialFilePath;
            var dir = Path.GetDirectoryName(path)!;
            Directory.CreateDirectory(dir);

            // Merge with existing so partial saves don't wipe sibling fields.
            var existing = Load();
            var merged = new StoredCredentials
            {
                AppKey = appKey ?? existing?.AppKey,
                AppSecret = Encrypt(appSecret ?? existing?.AppSecret),
                RefreshToken = Encrypt(refreshToken ?? existing?.RefreshToken),
                SavedAt = DateTimeOffset.UtcNow
            };

            var json = JsonSerializer.Serialize(merged, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(path, json);

            TryRestrictPermissions(path);
        }

        public static bool Clear()
        {
            var path = CredentialFilePath;
            if (!File.Exists(path)) return false;
            File.Delete(path);
            return true;
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

            if (stored.StartsWith("dpapi:", StringComparison.Ordinal))
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
                    File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                }
            }
            catch
            {
                // Best-effort; ignore.
            }
        }
    }
}
