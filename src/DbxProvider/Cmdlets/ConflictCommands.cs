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
    /// "conflicted copy" files. The first run does a full recursive
    /// enumeration and persists the resulting cursor and match set to a JSON
    /// sidecar file; later runs fetch only the delta since that cursor,
    /// transparently falling back to a full pass when the cursor is rejected
    /// or any scan parameter changes. Pass -Full to force a full pass.
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

            // Persist progress periodically during a long full enumeration so an
            // interruption (crash/Ctrl+C/network drop) resumes from the saved
            // cursor on the next run instead of restarting the whole scan.
            var sinceSave = System.Diagnostics.Stopwatch.StartNew();
            var saveInterval = TimeSpan.FromSeconds(15);
            var result = Run(ct => scanner.ScanAsync(parameters, previousState, ct, progressState =>
            {
                if (sinceSave.Elapsed < saveInterval) return;
                SaveState(statePath, progressState);
                sinceSave.Restart();
            }));

            SaveState(statePath, result.State);

            WriteVerbose(result.WasFullScan
                ? $"Full recursive scan complete: {result.Matches.Count} match(es). State saved to '{statePath}'."
                : $"Incremental scan complete: {result.Matches.Count} match(es). State saved to '{statePath}'.");

            foreach (var match in result.Matches.OrderBy(m => m.Path, StringComparer.OrdinalIgnoreCase))
                WriteObject(match);
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