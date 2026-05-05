using System;

namespace DbxProvider.Services
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

        /// <summary>Soft cap on in-memory entries; LRU eviction when exceeded.</summary>
        public int MaxEntries { get; set; } = 10_000;

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
