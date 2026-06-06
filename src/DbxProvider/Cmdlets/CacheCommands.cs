using System;
using System.Linq;
using System.Management.Automation;
using DbxProvider.Provider;
using IntelliTect.Dropbox;

namespace DbxProvider.Cmdlets
{
    internal static class CacheCmdletHelpers
    {
        public static MetadataCache GetCache(PSCmdlet cmdlet, string driveName)
        {
            var drive = cmdlet.SessionState.Drive.Get(driveName);
            if (drive is not DropboxDriveInfo dbx)
                throw new InvalidOperationException(
                    $"Drive '{driveName}:' is not a Dropbox drive. Run Connect-Dropbox first.");
            if (dbx.Cache == null)
                throw new InvalidOperationException(
                    $"Drive '{driveName}:' has no metadata cache. Reconnect with Connect-Dropbox.");
            return dbx.Cache;
        }
    }

    /// <summary>
    /// Surfaces the current state of the Dropbox metadata cache for
    /// observability: paths, item counts, cursor age, dirty state.
    /// </summary>
    [Cmdlet(VerbsCommon.Get, "DropboxCacheInfo")]
    [OutputType(typeof(PSObject))]
    public class GetDropboxCacheInfoCommand : PSCmdlet
    {
        [Parameter(Position = 0)]
        public string DriveName { get; set; } = "Dbx";

        protected override void ProcessRecord()
        {
            var cache = CacheCmdletHelpers.GetCache(this, DriveName);

            var summary = new PSObject();
            summary.Properties.Add(new PSNoteProperty("DriveName", DriveName));
            summary.Properties.Add(new PSNoteProperty("AccountId", cache.AccountId));
            summary.Properties.Add(new PSNoteProperty("Email", cache.Email));
            summary.Properties.Add(new PSNoteProperty("CacheDirectory", cache.AccountDirectory));
            summary.Properties.Add(new PSNoteProperty("DatabasePath", cache.DatabasePath));
            summary.Properties.Add(new PSNoteProperty("InMemoryEntryCount", cache.Count));
            summary.Properties.Add(new PSNoteProperty("PersistedEntryCount", cache.PersistedCount()));
            summary.Properties.Add(new PSNoteProperty("Enabled", cache.Options.Enabled));
            summary.Properties.Add(new PSNoteProperty("MaxInMemoryEntries", cache.Options.MaxInMemoryEntries));
            summary.Properties.Add(new PSNoteProperty("FlushIntervalSeconds", cache.Options.FlushIntervalSeconds));
            WriteObject(summary);

            foreach (var entry in cache.SnapshotInfo().OrderBy(e => e.Path))
            {
                var row = new PSObject();
                row.Properties.Add(new PSNoteProperty("Path", entry.Path));
                row.Properties.Add(new PSNoteProperty("ItemCount", entry.ItemCount));
                row.Properties.Add(new PSNoteProperty("LastValidatedUtc", entry.LastValidatedUtc));
                row.Properties.Add(new PSNoteProperty("LastUsedUtc", entry.LastUsedUtc));
                row.Properties.Add(new PSNoteProperty("InMemory", entry.InMemory));
                row.Properties.Add(new PSNoteProperty("Dirty", entry.Dirty));
                row.Properties.Add(new PSNoteProperty("CursorPreview",
                    string.IsNullOrEmpty(entry.Cursor)
                        ? ""
                        : entry.Cursor.Substring(0, Math.Min(16, entry.Cursor.Length)) + "..."));
                WriteObject(row);
            }
        }
    }

    /// <summary>Drops a single path's cache entry, or all entries on the drive.</summary>
    [Cmdlet(VerbsCommon.Clear, "DropboxCache")]
    public class ClearDropboxCacheCommand : PSCmdlet
    {
        [Parameter(Position = 0)]
        public string? Path { get; set; }

        [Parameter]
        public string DriveName { get; set; } = "Dbx";

        protected override void ProcessRecord()
        {
            var cache = CacheCmdletHelpers.GetCache(this, DriveName);
            cache.Clear(Path);
            WriteVerbose(Path == null
                ? $"Cleared all cache entries for drive '{DriveName}:'."
                : $"Cleared cache entry for '{Path}' on drive '{DriveName}:'.");
        }
    }

    /// <summary>Eagerly run validate+merge for a path (or all paths).</summary>
    [Cmdlet(VerbsData.Update, "DropboxCache")]
    public class UpdateDropboxCacheCommand : DropboxCmdletBase
    {
        [Parameter(Position = 0)]
        public string? Path { get; set; }

        protected override void ProcessRecord()
        {
            var cache = CacheCmdletHelpers.GetCache(this, DriveName);
            Run(ct => cache.UpdateAsync(Path, cancellationToken: ct));
        }
    }

    /// <summary>Toggles cache options at runtime.</summary>
    [Cmdlet(VerbsCommon.Set, "DropboxCacheOption")]
    public class SetDropboxCacheOptionCommand : PSCmdlet
    {
        [Parameter]
        public SwitchParameter Disable { get; set; }

        [Parameter]
        public SwitchParameter Enable { get; set; }

        [Parameter]
        [ValidateRange(0, int.MaxValue)]
        public int? MaxInMemoryEntries { get; set; }

        [Parameter]
        [ValidateRange(0, int.MaxValue)]
        public int? FlushIntervalSeconds { get; set; }

        [Parameter]
        public string DriveName { get; set; } = "Dbx";

        protected override void ProcessRecord()
        {
            var cache = CacheCmdletHelpers.GetCache(this, DriveName);
            if (Disable.IsPresent) cache.Options.Enabled = false;
            if (Enable.IsPresent) cache.Options.Enabled = true;
            if (MaxInMemoryEntries.HasValue) cache.Options.MaxInMemoryEntries = MaxInMemoryEntries.Value;
            if (FlushIntervalSeconds.HasValue) cache.Options.FlushIntervalSeconds = FlushIntervalSeconds.Value;

            // Also push these to the static Default so a subsequent Connect-Dropbox
            // (which constructs a new MetadataCache from CacheOptions.Default) inherits them.
            if (Disable.IsPresent) CacheOptions.Default.Enabled = false;
            if (Enable.IsPresent) CacheOptions.Default.Enabled = true;
            if (MaxInMemoryEntries.HasValue) CacheOptions.Default.MaxInMemoryEntries = MaxInMemoryEntries.Value;
            if (FlushIntervalSeconds.HasValue) CacheOptions.Default.FlushIntervalSeconds = FlushIntervalSeconds.Value;

            WriteObject(new PSObject(cache.Options));
        }
    }
}
