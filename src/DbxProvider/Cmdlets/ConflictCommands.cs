using System;
using System.IO;
using System.Linq;
using System.Management.Automation;
using System.Security.Cryptography;
using System.Text;
using IntelliTect.Dropbox;

namespace DbxProvider.Cmdlets
{
    /// <summary>
    /// Finds zero-byte (or, with -IncludeNonZero, all) "conflicted copy" files
    /// under a Dropbox subtree by reading the local metadata cache -- no recursive
    /// Dropbox enumeration. The cache is auto-refreshed from the account delta
    /// cursor first (the shared GetRefreshedCache), so results reflect changes
    /// since the last sync. This delegates to the same cache finder as
    /// Find-DropboxItem, fixing the hard-coded conflict pattern and the zero-byte
    /// filter. Build or refresh the cache with Build-DropboxCacheAll.ps1. A legacy
    /// *.state.json sidecar from an earlier version is archived to .bak on sight.
    /// </summary>
    [Cmdlet(VerbsCommon.Find, "DropboxConflict")]
    [OutputType(typeof(ConflictMatch))]
    public class FindDropboxConflictCommand : DropboxCmdletBase
    {
        /// <summary>Dropbox path (or drive path such as <c>Dbx:\Folder</c>) to scan. Defaults to the account root.</summary>
        [Parameter(Position = 0)]
        public string Path { get; set; } = string.Empty;

        /// <summary>Filename wildcard identifying a conflict file.</summary>
        [Parameter]
        public string Pattern { get; set; } = "*'s conflicted copy*";

        /// <summary>Also capture conflict files that are not zero bytes.</summary>
        [Parameter]
        public SwitchParameter IncludeNonZero { get; set; }

        /// <summary>Path to a legacy <c>*.state.json</c> sidecar to migrate. When
        /// omitted, the obsolete per-account default location is checked. Any such
        /// sidecar is archived to <c>.bak</c>; conflict finding itself is
        /// cache-backed and needs no sidecar.</summary>
        [Parameter]
        public string? StatePath { get; set; }

        /// <summary>Reads conflicts from the auto-refreshed cache and emits each match.</summary>
        protected override void ProcessRecord()
        {
            var startPath = StripDrivePrefix(Path);
            var cache = GetRefreshedCache();

            MigrateLegacyStateIfPresent(startPath);

            if (cache.PersistedCount() == 0)
            {
                // Match Find-DropboxItem: an empty cache looks like "no conflicts"
                // when the real issue is the cache was never built. Say so.
                WriteWarning(
                    "The metadata cache is empty. Run Build-DropboxCacheAll.ps1 (or " +
                    "Build-DropboxCache) to populate it before scanning for conflicts.");
                return;
            }

            // Conflict matches are always files (never folders), preserving the
            // original files-only semantics that make the result safe to delete.
            var namePredicate = FindDropboxItemCommand.BuildNamePredicate(
                Pattern, zeroByteOnly: !IncludeNonZero.IsPresent);
            var matches = cache.FindItems(
                item => !item.IsFolder && namePredicate(item), startPath);

            WriteVerbose($"Found {matches.Count} conflict match(es) from the metadata cache.");

            foreach (var item in matches.OrderBy(m => m.Path, StringComparer.OrdinalIgnoreCase))
                WriteObject(new ConflictMatch { Path = item.Path, Bytes = item.Length });
        }

        /// <summary>Detects and archives an obsolete conflict-scan sidecar (the
        /// pre-cache persisted state) so an upgraded user neither errors nor
        /// silently loses their saved matches. Both locations an older version
        /// could have used are checked -- the explicit <c>-StatePath</c> (when
        /// supplied) and the per-account default temp path -- so passing
        /// <c>-StatePath</c> never leaves the old default sidecar behind. Each is
        /// moved to a unique <c>.bak</c>; the cache is now authoritative.</summary>
        private void MigrateLegacyStateIfPresent(string startPath)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrWhiteSpace(StatePath) && seen.Add(StatePath!))
                TryMigrateLegacyState(StatePath!);

            var defaultPath = LegacyDefaultStatePath(startPath);
            if (seen.Add(defaultPath))
                TryMigrateLegacyState(defaultPath);
        }

        /// <summary>Archives a single obsolete sidecar at <paramref name="candidate"/>
        /// when it exists and parses as legacy state; otherwise leaves it untouched.</summary>
        private void TryMigrateLegacyState(string candidate)
        {
            try
            {
                if (!File.Exists(candidate)) return;
                var legacy = LegacyConflictScanState.FromJson(File.ReadAllText(candidate));
                if (legacy == null) return; // not a legacy sidecar -- leave it untouched

                var archive = UniqueBackupPath(candidate);
                File.Move(candidate, archive);
                WriteWarning(
                    $"Found an obsolete conflict-scan sidecar '{candidate}' " +
                    $"({legacy.Matches.Count} saved match(es)). Conflict finding is now backed by the " +
                    $"metadata cache, so the sidecar was archived to '{archive}'. Build or refresh the " +
                    $"cache with Build-DropboxCacheAll.ps1.");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                WriteWarning($"Could not migrate legacy scan state '{candidate}': {ex.Message}.");
            }
        }

        private string LegacyDefaultStatePath(string startPath) =>
            LegacyDefaultStatePath(DriveName, startPath, Pattern, IncludeNonZero.IsPresent);

        /// <summary>Computes the obsolete per-account default sidecar path
        /// (<c>%TEMP%\DbxProvider\conflict-scan-&lt;hash&gt;.json</c>) for the
        /// given finder inputs. Exposed to host tests so they can assert the exact
        /// legacy location is migrated.</summary>
        internal static string LegacyDefaultStatePath(
            string driveName, string startPath, string pattern, bool includeNonZero)
        {
            var key = $"{driveName}|{DropboxServiceClient.NormalizePath(startPath)}|{pattern}|{includeNonZero}";
            var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "DbxProvider");
            return System.IO.Path.Combine(dir, $"conflict-scan-{ShortHash(key)}.json");
        }

        private static string UniqueBackupPath(string path)
        {
            var bak = path + ".bak";
            int n = 1;
            while (File.Exists(bak)) bak = $"{path}.bak{n++}";
            return bak;
        }

        private static string ShortHash(string value)
        {
            using var sha = SHA256.Create();
            var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(value));
            var sb = new StringBuilder(16);
            for (int i = 0; i < 8; i++) sb.Append(bytes[i].ToString("x2"));
            return sb.ToString();
        }
    }
}