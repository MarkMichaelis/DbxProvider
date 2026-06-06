using System;
using System.Collections.Generic;

namespace IntelliTect.Dropbox
{
    /// <summary>
    /// Tunable options that govern the metadata cache. A single process-wide
    /// instance is exposed via <see cref="Default"/>; the
    /// <c>Set-DropboxCacheOption</c> cmdlet mutates that instance. Tests may
    /// construct their own to avoid touching global state.
    /// </summary>
    public sealed class CacheOptions
    {
        /// <summary>When false, every cache lookup is a miss and every read
        /// goes through to the Dropbox API.</summary>
        public bool Enabled { get; set; } = true;

        /// <summary>
        /// Soft budget on how many entries stay resident in memory. The
        /// persistent cache (the on-disk SQLite database) is never capped; when
        /// this budget is exceeded the least-recently-used entries are flushed
        /// to disk and dropped from memory only, then re-hydrated on demand.
        /// Set to 0 to keep every loaded entry resident (no spilling).
        /// </summary>
        public int MaxInMemoryEntries { get; set; } = 50_000;

        /// <summary>Background disk-flush cadence. Set to 0 to disable.</summary>
        public int FlushIntervalSeconds { get; set; } = 5;

        /// <summary>Override the on-disk cache root. The single per-account
        /// database file (named from the account email) is placed directly in
        /// this directory.</summary>
        public string? RootDirectoryOverride { get; set; }

        public string EffectiveRootDirectory =>
            RootDirectoryOverride ??
            System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "DbxProvider", "cache");

        /// <summary>
        /// Per-account override of the metadata cache database file path, keyed
        /// by Dropbox account email. Lookups ignore case. When a connecting
        /// account's email has an entry here, its cache database is placed at
        /// exactly that path (after <c>~</c>/environment-variable expansion)
        /// instead of the default <c>&lt;cacheRoot&gt;\DropboxCache.&lt;email&gt;.db</c>.
        /// The concrete mapping is user runtime configuration; it is never baked
        /// into the library.
        /// </summary>
        public IDictionary<string, string> EmailDatabasePathOverrides { get; } =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Process-wide options instance. Persisted per-email database path
        /// overrides (see <see cref="CacheConfigStore"/>) are loaded into this
        /// instance on first access so they survive across PowerShell sessions.
        /// </summary>
        public static CacheOptions Default { get; } = CreateDefault();

        private static CacheOptions CreateDefault()
        {
            var options = new CacheOptions();
            foreach (var pair in CacheConfigStore.Default.LoadOverrides())
            {
                options.EmailDatabasePathOverrides[pair.Key] = pair.Value;
            }

            return options;
        }
    }
}
