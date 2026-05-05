using System;
using System.IO;
using System.Management.Automation;
using Dropbox.Api.Files;
using DbxProvider.Models;
using DbxProvider.Services;

namespace DbxProvider.Cmdlets
{
    /// <summary>Downloads a file from Dropbox to local disk.</summary>
    [Cmdlet(VerbsLifecycle.Invoke, "DropboxDownload")]
    [OutputType(typeof(FileInfo))]
    public class InvokeDropboxDownloadCommand : DropboxCmdletBase
    {
        [Parameter(Mandatory = true, Position = 0, ValueFromPipeline = true,
            ValueFromPipelineByPropertyName = true)]
        [Alias("FullName")]
        public string Path { get; set; } = string.Empty;

        [Parameter(Mandatory = true, Position = 1)]
        public string Destination { get; set; } = string.Empty;

        [Parameter]
        public SwitchParameter Force { get; set; }

        protected override void ProcessRecord()
        {
            try
            {
                var resolvedDest = GetUnresolvedProviderPathFromPSPath(Destination);

                if (File.Exists(resolvedDest) && !Force)
                {
                    WriteError(new ErrorRecord(
                        new IOException($"File '{resolvedDest}' already exists. Use -Force to overwrite."),
                        "FileExists", ErrorCategory.ResourceExists, resolvedDest));
                    return;
                }

                var service = GetService();
                var bytes = Run(ct => service.DownloadBytesAsync(Path, cancellationToken: ct));

                // Ensure destination directory exists
                var dir = System.IO.Path.GetDirectoryName(resolvedDest);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                File.WriteAllBytes(resolvedDest, bytes);
                WriteObject(new FileInfo(resolvedDest));
                WriteVerbose($"Downloaded {Path} to {resolvedDest} ({bytes.Length:N0} bytes)");
            }
            catch (Exception ex) when (ex is not PipelineStoppedException)
            {
                WriteError(new ErrorRecord(ex, "DownloadFailed",
                    ErrorCategory.WriteError, Path));
            }
        }
    }

    /// <summary>Uploads a local file to Dropbox with large-file support.</summary>
    [Cmdlet(VerbsLifecycle.Invoke, "DropboxUpload")]
    [OutputType(typeof(DropboxItem))]
    public class InvokeDropboxUploadCommand : DropboxCmdletBase
    {
        [Parameter(Mandatory = true, Position = 0)]
        public string Source { get; set; } = string.Empty;

        [Parameter(Mandatory = true, Position = 1)]
        public string DropboxPath { get; set; } = string.Empty;

        [Parameter]
        public SwitchParameter Force { get; set; }

        [Parameter]
        [ValidateSet("add", "overwrite", "update")]
        public string WriteMode { get; set; } = "overwrite";

        protected override void ProcessRecord()
        {
            try
            {
                var resolvedSource = GetUnresolvedProviderPathFromPSPath(Source);

                if (!File.Exists(resolvedSource))
                {
                    WriteError(new ErrorRecord(
                        new FileNotFoundException($"File '{resolvedSource}' not found."),
                        "FileNotFound", ErrorCategory.ObjectNotFound, resolvedSource));
                    return;
                }

                Dropbox.Api.Files.WriteMode mode = WriteMode.ToLowerInvariant() switch
                {
                    "add" => Dropbox.Api.Files.WriteMode.Add.Instance,
                    "overwrite" => Dropbox.Api.Files.WriteMode.Overwrite.Instance,
                    _ => Dropbox.Api.Files.WriteMode.Overwrite.Instance
                };

                var service = GetService();
                using var stream = File.OpenRead(resolvedSource);
                var item = Run(ct => service.UploadAsync(DropboxPath, stream, mode, cancellationToken: ct));
                WriteObject(item);

                WriteVerbose($"Uploaded {resolvedSource} to {item.Path} ({item.Size:N0} bytes)");
            }
            catch (Exception ex) when (ex is not PipelineStoppedException)
            {
                WriteError(new ErrorRecord(ex, "UploadFailed",
                    ErrorCategory.WriteError, Source));
            }
        }
    }
}