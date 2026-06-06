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
    /// Scans a Dropbox subtree for zero-byte (or, with -IncludeNonZero, all)
    /// "conflicted copy" files. A cold run uses the fast indexed search_v2
    /// endpoint by default (like the Dropbox website). When a reusable saved
    /// cursor exists, later runs fetch only the delta since that cursor. Pass
    /// -Full to force an authoritative recursive enumeration that also
    /// (re)establishes the incremental cursor.
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

        /// <summary>Path to the JSON sidecar state file. Defaults to a per-account/path/pattern file under the temp folder.</summary>
        [Parameter]
        public string? StatePath { get; set; }

        /// <summary>Ignore any saved state and force a full recursive enumeration.</summary>
        [Parameter]
        public SwitchParameter Full { get; set; }

        /// <summary>Runs the scan, persists updated state, and emits each match.</summary>
        protected override void ProcessRecord()
        {
            var service = GetService();
            var startPath = StripDrivePrefix(Path);
            var parameters = new ConflictScanParameters
            {
                StartPath = startPath,
                Pattern = Pattern,
                IncludeNonZero = IncludeNonZero.IsPresent,
            };

            var statePath = ResolveStatePath(startPath, parameters);
            var previousState = Full.IsPresent ? null : LoadState(statePath);

            var scanner = new ConflictScanner(service);
            var (result, mode) = RunScan(scanner, parameters, previousState);

            SaveState(statePath, result.State);

            WriteVerbose($"{mode} complete: {result.Matches.Count} match(es). State saved to '{statePath}'.");

            foreach (var match in result.Matches.OrderBy(m => m.Path, StringComparer.OrdinalIgnoreCase))
                WriteObject(match);
        }

        /// <summary>
        /// Routes to the right scan strategy: <c>-Full</c> forces an authoritative
        /// recursive walk; a reusable saved cursor drives a warm incremental delta;
        /// otherwise a cold run uses the fast search_v2 discovery path.
        /// </summary>
        private (ConflictScanResult Result, string Mode) RunScan(
            ConflictScanner scanner, ConflictScanParameters parameters, ConflictScanState? previousState)
        {
            if (Full.IsPresent)
                return (Run(ct => scanner.ScanAsync(parameters, null, ct)), "Full recursive scan");

            if (previousState is not null && !string.IsNullOrEmpty(previousState.Cursor))
            {
                var warm = Run(ct => scanner.ScanAsync(parameters, previousState, ct));
                return (warm, warm.WasFullScan ? "Full recursive scan" : "Incremental scan");
            }

            return (Run(ct => scanner.SearchScanAsync(parameters, ct)), "Search scan");
        }

        private static string StripDrivePrefix(string path)
        {
            if (string.IsNullOrEmpty(path)) return string.Empty;
            int colon = path.IndexOf(':');
            return colon >= 0 ? path.Substring(colon + 1) : path;
        }

        private string ResolveStatePath(string startPath, ConflictScanParameters parameters)
        {
            if (!string.IsNullOrWhiteSpace(StatePath)) return StatePath!;
            var key = $"{DriveName}|{DropboxServiceClient.NormalizePath(startPath)}|{parameters.Pattern}|{parameters.IncludeNonZero}";
            var hash = ShortHash(key);
            var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "DbxProvider");
            return System.IO.Path.Combine(dir, $"conflict-scan-{hash}.json");
        }

        private static string ShortHash(string value)
        {
            using var sha = SHA256.Create();
            var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(value));
            var sb = new StringBuilder(16);
            for (int i = 0; i < 8; i++) sb.Append(bytes[i].ToString("x2"));
            return sb.ToString();
        }

        private ConflictScanState? LoadState(string statePath)
        {
            try
            {
                if (!File.Exists(statePath)) return null;
                return ConflictScanState.FromJson(File.ReadAllText(statePath));
            }
            catch (IOException ex)
            {
                WriteWarning($"Could not read scan state '{statePath}': {ex.Message}. Doing a full scan.");
                return null;
            }
        }

        private void SaveState(string statePath, ConflictScanState state)
        {
            var dir = System.IO.Path.GetDirectoryName(statePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(statePath, state.ToJson(), new UTF8Encoding(false));
        }
    }
}