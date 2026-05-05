using System;
using System.IO;
using System.Management.Automation;

namespace DbxProvider.Cmdlets
{
    /// <summary>Gets a preview (PDF) for a Dropbox file.</summary>
    [Cmdlet(VerbsCommon.Get, "DropboxPreview")]
    [OutputType(typeof(byte[]), typeof(FileInfo))]
    public class GetDropboxPreviewCommand : DropboxCmdletBase
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
                var (content, contentType) = Run(ct => service.GetPreviewAsync(Path, cancellationToken: ct));

                if (!string.IsNullOrEmpty(OutFile))
                {
                    var resolved = GetUnresolvedProviderPathFromPSPath(OutFile);
                    File.WriteAllBytes(resolved, content);
                    WriteObject(new FileInfo(resolved));
                }
                else
                {
                    WriteObject(content);
                }
            }
            catch (Exception ex)
            {
                WriteError(new ErrorRecord(ex, "GetPreviewFailed",
                    ErrorCategory.ReadError, Path));
            }
        }
    }

    /// <summary>Gets a thumbnail for a Dropbox file.</summary>
    [Cmdlet(VerbsCommon.Get, "DropboxThumbnail")]
    [OutputType(typeof(byte[]), typeof(FileInfo))]
    public class GetDropboxThumbnailCommand : DropboxCmdletBase
    {
        [Parameter(Mandatory = true, Position = 0, ValueFromPipeline = true,
            ValueFromPipelineByPropertyName = true)]
        [Alias("FullName")]
        public string Path { get; set; } = string.Empty;

        [Parameter]
        [ValidateSet("w32h32", "w64h64", "w128h128", "w256h256", "w480h320",
            "w640h480", "w960h640", "w1024h768", "w2048h1536")]
        public string Size { get; set; } = "w64h64";

        [Parameter]
        [ValidateSet("jpeg", "png")]
        public string Format { get; set; } = "jpeg";

        [Parameter]
        public string? OutFile { get; set; }

        protected override void ProcessRecord()
        {
            try
            {
                var service = GetService();
                var content = Run(ct => service.GetThumbnailAsync(Path, Size, Format, cancellationToken: ct));

                if (!string.IsNullOrEmpty(OutFile))
                {
                    var resolved = GetUnresolvedProviderPathFromPSPath(OutFile);
                    File.WriteAllBytes(resolved, content);
                    WriteObject(new FileInfo(resolved));
                }
                else
                {
                    WriteObject(content);
                }
            }
            catch (Exception ex)
            {
                WriteError(new ErrorRecord(ex, "GetThumbnailFailed",
                    ErrorCategory.ReadError, Path));
            }
        }
    }
}