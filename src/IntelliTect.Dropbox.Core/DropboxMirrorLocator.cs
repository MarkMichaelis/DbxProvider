using System;
using System.IO;
using System.Text.Json;

namespace IntelliTect.Dropbox
{
    /// <summary>
    /// Discovers the local Dropbox folder that the Dropbox desktop client keeps
    /// in sync, by reading the client's <c>info.json</c> descriptor. This is the
    /// default local-mirror root used when a caller does not specify one
    /// explicitly (for example a NAS share).
    /// </summary>
    public static class DropboxMirrorLocator
    {
        /// <summary>
        /// Locates the local Dropbox root by reading <c>info.json</c> from the
        /// standard locations (<c>%LOCALAPPDATA%\Dropbox\info.json</c> and
        /// <c>%APPDATA%\Dropbox\info.json</c>). Returns <see langword="null"/>
        /// when the Dropbox client is not installed or the file cannot be read,
        /// so callers fall back to API downloads.
        /// </summary>
        public static string? FindLocalRoot()
        {
            foreach (string infoPath in CandidateInfoJsonPaths())
            {
                string? root = ReadRootFromInfoJson(infoPath);
                if (!string.IsNullOrEmpty(root))
                {
                    return root;
                }
            }

            return null;
        }

        /// <summary>
        /// Reads the local Dropbox folder path from the <c>info.json</c> file at
        /// <paramref name="infoJsonPath"/>, preferring the personal account over
        /// a business account. Returns <see langword="null"/> when the file is
        /// missing, malformed, or contains no usable path.
        /// </summary>
        /// <param name="infoJsonPath">Path to a Dropbox <c>info.json</c> file.</param>
        public static string? ReadRootFromInfoJson(string infoJsonPath)
        {
            if (string.IsNullOrEmpty(infoJsonPath) || !File.Exists(infoJsonPath))
            {
                return null;
            }

            try
            {
                using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(infoJsonPath));
                return ReadAccountPath(doc.RootElement, "personal")
                    ?? ReadAccountPath(doc.RootElement, "business");
            }
            catch (JsonException)
            {
                return null;
            }
            catch (IOException)
            {
                return null;
            }
        }

        private static string? ReadAccountPath(JsonElement root, string accountKind)
        {
            if (root.ValueKind == JsonValueKind.Object &&
                root.TryGetProperty(accountKind, out JsonElement account) &&
                account.ValueKind == JsonValueKind.Object &&
                account.TryGetProperty("path", out JsonElement path) &&
                path.ValueKind == JsonValueKind.String)
            {
                string? value = path.GetString();
                return string.IsNullOrEmpty(value) ? null : value;
            }

            return null;
        }

        private static string[] CandidateInfoJsonPaths()
        {
            return new[]
            {
                Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Dropbox", "info.json"),
                Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "Dropbox", "info.json"),
            };
        }
    }
}
