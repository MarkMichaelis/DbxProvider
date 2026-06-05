using System;

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

        /// <summary>Override the on-disk cache root (per-account subdir is appended).</summary>
        public string? RootDirectoryOverride { get; set; }

        public string EffectiveRootDirectory =>
            RootDirectoryOverride ??
            System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "DbxProvider", "cache");

        public static CacheOptions Default { get; } = new CacheOptions();
    }
}
