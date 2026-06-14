using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Management.Automation;
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
    [Cmdlet(VerbsCommon.Copy, "DropboxItemBatch")]
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

                var service = GetService();
                var items = Run(ct => service.CopyBatchAsync(entries, cancellationToken: ct));
                foreach (var item in items)
                {
                    WriteObject(item);
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
    [Cmdlet(VerbsCommon.Move, "DropboxItemBatch")]
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

                var service = GetService();
                var items = Run(ct => service.MoveBatchAsync(entries, cancellationToken: ct));
                foreach (var item in items)
                {
                    WriteObject(item);
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
                foreach (var chunk in Chunk(_paths, DropboxServiceClient.MaxDeleteBatchSize))
                {
                    var failures = Run(ct => service.DeleteBatchAsync(chunk, cancellationToken: ct));
                    foreach (var failure in failures)
                    {
                        failureCount++;
                        WriteError(new ErrorRecord(
                            new InvalidOperationException($"Could not delete '{failure.Path}': {failure.Reason}"),
                            "DeleteBatchEntryFailed", ErrorCategory.WriteError, failure.Path));
                    }
                }
                WriteVerbose($"Batch deleted {_paths.Count - failureCount} of {_paths.Count} items");
            }
            catch (Exception ex) when (ex is not PipelineStoppedException)
            {
                WriteError(new ErrorRecord(ex, "DeleteBatchFailed",
                    ErrorCategory.WriteError, null));
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