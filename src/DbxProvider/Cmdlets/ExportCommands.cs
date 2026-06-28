using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Management.Automation;
using System.Threading;
using IntelliTect.Dropbox;

namespace DbxProvider.Cmdlets
{
    /// <summary>Exports a Dropbox file (e.g., Google Docs, Sheets) to a downloadable format.</summary>
    [Cmdlet(VerbsData.Export, "DropboxFile")]
    [OutputType(typeof(byte[]), typeof(FileInfo))]
    public class ExportDropboxFileCommand : DropboxCmdletBase
    {
        [Parameter(Mandatory = true, Position = 0, ValueFromPipeline = true,
            ValueFromPipelineByPropertyName = true)]
        [Alias("FullName")]
        public string Path { get; set; } = string.Empty;

        [Parameter]
        public string? OutFile { get; set; }

        protected override void ProcessRecord()
        {
            try
            {
                var service = GetService();
                var (content, metadata) = Run(ct => service.ExportFileAsync(Path, cancellationToken: ct));

                if (!string.IsNullOrEmpty(OutFile))
                {
                    var resolved = GetUnresolvedProviderPathFromPSPath(OutFile);
                    File.WriteAllBytes(resolved, content);
                    WriteObject(new FileInfo(resolved));
                    WriteVerbose($"Exported {Path} to {resolved}");
                }
                else
                {
                    WriteObject(content);
                }
            }
            catch (Exception ex)
            {
                WriteError(new ErrorRecord(ex, "ExportFailed",
                    ErrorCategory.ReadError, Path));
            }
        }
    }

    /// <summary>Performs batch copy operations in Dropbox.</summary>
    [Cmdlet(VerbsCommon.Copy, "DropboxItemBatch", SupportsShouldProcess = true)]
    [OutputType(typeof(DropboxItem))]
    public class CopyDropboxItemBatchCommand : DropboxCmdletBase
    {
        [Parameter(Mandatory = true, Position = 0)]
        public string[] FromPath { get; set; } = Array.Empty<string>();

        [Parameter(Mandatory = true, Position = 1)]
        public string[] ToPath { get; set; } = Array.Empty<string>();

        protected override void ProcessRecord()
        {
            try
            {
                if (FromPath.Length != ToPath.Length)
                {
                    ThrowTerminatingError(new ErrorRecord(
                        new ArgumentException("FromPath and ToPath arrays must have the same length."),
                        "ArrayLengthMismatch", ErrorCategory.InvalidArgument, null));
                    return;
                }

                var entries = new (string from, string to)[FromPath.Length];
                for (int i = 0; i < FromPath.Length; i++)
                {
                    entries[i] = (FromPath[i], ToPath[i]);
                }

                if (!ShouldProcess(string.Join(", ", FromPath), "Batch copy")) return;

                var service = GetService();
                var result = Run(ct => service.CopyBatchAsync(entries, cancellationToken: ct));
                foreach (var item in result.Items)
                {
                    WriteObject(item);
                }
                foreach (var failure in result.Failures)
                {
                    WriteError(new ErrorRecord(
                        new InvalidOperationException($"Batch copy entry failed for '{failure.FromPath}' -> '{failure.ToPath}': {failure.Reason}"),
                        "CopyBatchEntryFailed", ErrorCategory.WriteError, failure.FromPath));
                }
            }
            catch (Exception ex) when (ex is not PipelineStoppedException)
            {
                WriteError(new ErrorRecord(ex, "CopyBatchFailed",
                    ErrorCategory.WriteError, null));
            }
        }
    }

    /// <summary>Performs batch move operations in Dropbox.</summary>
    [Cmdlet(VerbsCommon.Move, "DropboxItemBatch", SupportsShouldProcess = true)]
    [OutputType(typeof(DropboxItem))]
    public class MoveDropboxItemBatchCommand : DropboxCmdletBase
    {
        [Parameter(Mandatory = true, Position = 0)]
        public string[] FromPath { get; set; } = Array.Empty<string>();

        [Parameter(Mandatory = true, Position = 1)]
        public string[] ToPath { get; set; } = Array.Empty<string>();

        protected override void ProcessRecord()
        {
            try
            {
                if (FromPath.Length != ToPath.Length)
                {
                    ThrowTerminatingError(new ErrorRecord(
                        new ArgumentException("FromPath and ToPath arrays must have the same length."),
                        "ArrayLengthMismatch", ErrorCategory.InvalidArgument, null));
                    return;
                }

                var entries = new (string from, string to)[FromPath.Length];
                for (int i = 0; i < FromPath.Length; i++)
                {
                    entries[i] = (FromPath[i], ToPath[i]);
                }

                if (!ShouldProcess(string.Join(", ", FromPath), "Batch move")) return;

                var service = GetService();
                var result = Run(ct => service.MoveBatchAsync(entries, cancellationToken: ct));
                foreach (var item in result.Items)
                {
                    WriteObject(item);
                }
                foreach (var failure in result.Failures)
                {
                    WriteError(new ErrorRecord(
                        new InvalidOperationException($"Batch move entry failed for '{failure.FromPath}' -> '{failure.ToPath}': {failure.Reason}"),
                        "MoveBatchEntryFailed", ErrorCategory.WriteError, failure.FromPath));
                }
            }
            catch (Exception ex) when (ex is not PipelineStoppedException)
            {
                WriteError(new ErrorRecord(ex, "MoveBatchFailed",
                    ErrorCategory.WriteError, null));
            }
        }
    }

    /// <summary>Performs batch delete operations in Dropbox.</summary>
    /// <remarks>
    /// <para><c>-Path</c> accepts bare API paths (<c>/Folder/file</c>),
    /// drive-qualified provider paths (<c>Dbx:\Folder\file</c>), or the
    /// <c>DropboxItem</c> objects emitted by <c>Search-Dropbox</c> -- so both
    /// <c>$items | Remove-DropboxItemBatch</c> and
    /// <c>$items.Path | Remove-DropboxItemBatch</c> work.</para>
    /// <para>All piped inputs are accumulated and deleted in a single batch
    /// call. Items the server could not delete (for example an already-deleted
    /// path) are reported as non-terminating errors rather than silently
    /// treated as successes.</para>
    /// </remarks>
    [Cmdlet(VerbsCommon.Remove, "DropboxItemBatch", SupportsShouldProcess = true)]
    public class RemoveDropboxItemBatchCommand : DropboxCmdletBase
    {
        /// <summary>The items or paths to delete.</summary>
        [Parameter(Mandatory = true, Position = 0, ValueFromPipeline = true)]
        public object[] Path { get; set; } = Array.Empty<object>();

        /// <summary>Skips removing the deleted items from the local metadata cache.</summary>
        [Parameter]
        public SwitchParameter SkipCacheUpdate { get; set; }

        /// <summary>
        /// Maximum number of Dropbox <c>delete_batch</c> jobs to run concurrently.
        /// Each batch is processed asynchronously server-side, so overlapping
        /// several in-flight jobs multiplies throughput. Defaults to 1 (serial).
        /// </summary>
        [Parameter]
        [ValidateRange(1, 32)]
        public int MaxConcurrency { get; set; } = 1;

        /// <summary>
        /// Paths per <c>delete_batch</c> API call. Smaller batches finish (and so
        /// advance the progress bar) more often, which keeps the bar visibly moving
        /// during the multi-minute server-side wait. This is independent of
        /// <see cref="MaxConcurrency"/>: shrinking the batch makes progress finer
        /// without adding the overlapping writes that cause namespace lock
        /// contention. Defaults to the <c>delete_batch</c> limit (1000).
        /// </summary>
        [Parameter]
        [ValidateRange(1, DropboxServiceClient.MaxDeleteBatchSize)]
        public int BatchSize { get; set; } = DropboxServiceClient.MaxDeleteBatchSize;

        private readonly List<string> _paths = new();

        /// <summary>Accumulates each piped item's path for a single batch delete.</summary>
        protected override void ProcessRecord()
        {
            foreach (var raw in Path)
            {
                var path = ExtractPath(raw);
                if (!string.IsNullOrWhiteSpace(path))
                {
                    _paths.Add(StripDrivePrefix(path));
                }
            }
        }

        /// <summary>Deletes every accumulated path in chunked batches and reports failures.</summary>
        protected override void EndProcessing()
        {
            if (_paths.Count == 0) return;
            try
            {
                if (!ShouldProcess(string.Join(", ", _paths), "Batch delete")) return;

                var service = GetService();
                int failureCount = 0;
                var failedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                void Report(DropboxBatchDeleteError failure)
                {
                    failureCount++;
                    failedPaths.Add(failure.Path);
                    WriteError(new ErrorRecord(
                        new InvalidOperationException($"Could not delete '{failure.Path}': {failure.Reason}"),
                        "DeleteBatchEntryFailed", ErrorCategory.WriteError, failure.Path));
                }

                if (MaxConcurrency > 1)
                {
                    var chunks = Chunk(_paths, BatchSize)
                        .Select(c => (IReadOnlyList<string>)c).ToList();
                    var failures = RunBatchesWithProgress(service, chunks);
                    foreach (var failure in failures)
                    {
                        Report(failure);
                    }
                }
                else
                {
                    foreach (var chunk in Chunk(_paths, BatchSize))
                    {
                        var failures = Run(ct => service.DeleteBatchAsync(chunk, cancellationToken: ct));
                        foreach (var failure in failures)
                        {
                            Report(failure);
                        }
                    }
                }
                UpdateCache(failedPaths);
                WriteVerbose($"Batch deleted {_paths.Count - failureCount} of {_paths.Count} items");
            }
            catch (Exception ex) when (ex is not PipelineStoppedException)
            {
                WriteError(new ErrorRecord(ex, "DeleteBatchFailed",
                    ErrorCategory.WriteError, null));
            }
        }

        /// <summary>
        /// Runs the chunked concurrent delete while streaming a live progress bar.
        /// A one-second timer ticks elapsed time so the bar never looks frozen
        /// during the long first batch, and each completed batch advances the
        /// item/batch counts. All UI writes are marshaled to the pipeline thread.
        /// </summary>
        private IReadOnlyList<DropboxBatchDeleteError> RunBatchesWithProgress(
            DropboxServiceClient service, IReadOnlyList<IReadOnlyList<string>> chunks)
        {
            int totalChunks = chunks.Count;
            int totalPaths = _paths.Count;
            int doneChunks = 0;
            int donePaths = 0;
            var started = System.Diagnostics.Stopwatch.StartNew();

            void Emit()
            {
                int dc = Volatile.Read(ref doneChunks);
                int dp = Volatile.Read(ref donePaths);
                var elapsed = started.Elapsed;
                var record = new ProgressRecord(
                    ProgressActivityId, "Removing Dropbox items",
                    $"{dp:N0}/{totalPaths:N0} processed, {dc}/{totalChunks} batch(es) done -- {elapsed:hh\\:mm\\:ss} elapsed")
                {
                    PercentComplete = totalPaths > 0 ? (int)Math.Min(100, 100L * dp / totalPaths) : 0,
                };
                EnqueueWrite(() => WriteProgress(record));
            }

            using var ticker = new System.Threading.Timer(_ => Emit(), null,
                TimeSpan.Zero, TimeSpan.FromSeconds(1));

            void OnChunk(int n)
            {
                Interlocked.Increment(ref doneChunks);
                Emit();
            }

            void OnItems(int n)
            {
                Interlocked.Add(ref donePaths, n);
                Emit();
            }

            try
            {
                return Run(ct => service.DeleteBatchesAsync(chunks, MaxConcurrency, OnChunk, OnItems, ct));
            }
            finally
            {
                ticker.Dispose();
                WriteProgress(new ProgressRecord(ProgressActivityId, "Removing Dropbox items", "Completed")
                {
                    RecordType = ProgressRecordType.Completed,
                });
            }
        }

        private const int ProgressActivityId = 1701;

        /// <summary>Removes every successfully-deleted path from the drive's metadata cache.</summary>
        private void UpdateCache(HashSet<string> failedPaths)
        {
            if (SkipCacheUpdate) return;
            MetadataCache? cache;
            try
            {
                cache = CacheCmdletHelpers.GetCache(this, DriveName);
            }
            catch (Exception ex)
            {
                WriteVerbose($"Skipping cache update: {ex.Message}");
                return;
            }
            foreach (var path in _paths)
            {
                if (!failedPaths.Contains(path))
                {
                    cache.ApplyLocalRemove(path);
                }
            }
        }

        /// <summary>Splits a list into successive sublists of at most <paramref name="size"/> items.</summary>
        private static IEnumerable<List<string>> Chunk(List<string> items, int size)
        {
            for (int i = 0; i < items.Count; i += size)
            {
                yield return items.GetRange(i, Math.Min(size, items.Count - i));
            }
        }

        private static string ExtractPath(object raw)
        {
            switch (raw)
            {
                case null:
                    return string.Empty;
                case string s:
                    return s;
                case DropboxItem item:
                    return item.Path;
                case PSObject pso:
                    if (pso.Properties["DropboxPath"]?.Value is string dbx && dbx.Length > 0) return dbx;
                    if (pso.Properties["Path"]?.Value is string path) return path;
                    return pso.BaseObject?.ToString() ?? string.Empty;
                default:
                    return raw.ToString() ?? string.Empty;
            }
        }
    }
}