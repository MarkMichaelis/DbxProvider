using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace IntelliTect.Dropbox
{
    /// <summary>
    /// PowerShell-agnostic persistence for cache settings that must survive
    /// across sessions. Currently stores the per-email database path overrides
    /// (see <see cref="CacheOptions.EmailDatabasePathOverrides"/>) in a JSON file
    /// named <c>config.json</c> under a configurable root directory. The root is
    /// constructor-injectable so unit tests can redirect it away from the real
    /// <c>%LOCALAPPDATA%</c>.
    /// </summary>
    public sealed class CacheConfigStore
    {
        private const string ConfigFileName = "config.json";

        private static readonly JsonSerializerOptions JsonOpts = new()
        {
            WriteIndented = true,
        };

        private readonly string _configDirectory;

        /// <summary>
        /// Creates a store rooted at <paramref name="configDirectory"/>. The
        /// config file is <c>&lt;configDirectory&gt;\config.json</c>.
        /// </summary>
        /// <param name="configDirectory">Directory that holds <c>config.json</c>.</param>
        public CacheConfigStore(string configDirectory)
        {
            _configDirectory = configDirectory ??
                throw new ArgumentNullException(nameof(configDirectory));
        }

        /// <summary>
        /// The default process-wide store rooted at
        /// <c>%LOCALAPPDATA%\DbxProvider</c>.
        /// </summary>
        public static CacheConfigStore Default { get; } = new CacheConfigStore(
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "DbxProvider"));

        /// <summary>Full path of the backing <c>config.json</c> file.</summary>
        public string ConfigFilePath => Path.Combine(_configDirectory, ConfigFileName);

        /// <summary>
        /// Loads the persisted email -> database-path overrides. Always returns a
        /// case-insensitive dictionary; a missing, empty, or corrupt file yields
        /// an empty map and never throws.
        /// </summary>
        public IDictionary<string, string> LoadOverrides()
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (!File.Exists(ConfigFilePath))
                {
                    return result;
                }

                var json = File.ReadAllText(ConfigFilePath);
                if (string.IsNullOrWhiteSpace(json))
                {
                    return result;
                }

                var model = JsonSerializer.Deserialize<ConfigModel>(json, JsonOpts);
                if (model?.EmailDatabasePathOverrides != null)
                {
                    foreach (var pair in model.EmailDatabasePathOverrides)
                    {
                        if (!string.IsNullOrWhiteSpace(pair.Key))
                        {
                            result[pair.Key] = pair.Value ?? "";
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                // Resilient by design: a corrupt or unreadable config must not
                // break cache initialization. Fall back to no overrides.
                return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }

            return result;
        }

        /// <summary>
        /// Persists the supplied email -> database-path overrides, replacing any
        /// previously stored map. The write is atomic: content is written to a
        /// temporary file and then moved into place.
        /// </summary>
        /// <param name="overrides">The overrides to persist.</param>
        public void SaveOverrides(IDictionary<string, string> overrides)
        {
            if (overrides == null)
            {
                throw new ArgumentNullException(nameof(overrides));
            }

            Directory.CreateDirectory(_configDirectory);

            var model = new ConfigModel
            {
                EmailDatabasePathOverrides =
                    new Dictionary<string, string>(overrides, StringComparer.OrdinalIgnoreCase),
            };
            var json = JsonSerializer.Serialize(model, JsonOpts);

            var tempPath = ConfigFilePath + ".tmp";
            File.WriteAllText(tempPath, json);
            if (File.Exists(ConfigFilePath))
            {
                File.Replace(tempPath, ConfigFilePath, null);
            }
            else
            {
                File.Move(tempPath, ConfigFilePath);
            }
        }

        private sealed class ConfigModel
        {
            public Dictionary<string, string>? EmailDatabasePathOverrides { get; set; }
        }
    }
}