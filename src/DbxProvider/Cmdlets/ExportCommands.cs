using System;
using System.IO;
using System.Management.Automation;
using DbxProvider.Models;

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
    [Cmdlet(VerbsCommon.Remove, "DropboxItemBatch", SupportsShouldProcess = true)]
    public class RemoveDropboxItemBatchCommand : DropboxCmdletBase
    {
        [Parameter(Mandatory = true, Position = 0, ValueFromPipeline = true)]
        public string[] Path { get; set; } = Array.Empty<string>();

        protected override void ProcessRecord()
        {
            try
            {
                if (ShouldProcess(string.Join(", ", Path), "Batch delete"))
                {
                    var service = GetService();
                    Run(ct => service.DeleteBatchAsync(Path, cancellationToken: ct));
                    WriteVerbose($"Batch deleted {Path.Length} items");
                }
            }
            catch (Exception ex) when (ex is not PipelineStoppedException)
            {
                WriteError(new ErrorRecord(ex, "DeleteBatchFailed",
                    ErrorCategory.WriteError, null));
            }
        }
    }
}