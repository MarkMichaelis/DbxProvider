using System;
using System.Management.Automation;
using DbxProvider.Models;

namespace DbxProvider.Cmdlets
{
    /// <summary>Adds a tag to a Dropbox file or folder.</summary>
    [Cmdlet(VerbsCommon.Add, "DropboxTag")]
    public class AddDropboxTagCommand : DropboxCmdletBase
    {
        [Parameter(Mandatory = true, Position = 0, ValueFromPipeline = true,
            ValueFromPipelineByPropertyName = true)]
        [Alias("FullName")]
        public string Path { get; set; } = string.Empty;

        [Parameter(Mandatory = true, Position = 1)]
        public string Tag { get; set; } = string.Empty;

        protected override void ProcessRecord()
        {
            try
            {
                var service = GetService();
                Run(ct => service.AddTagAsync(Path, Tag, cancellationToken: ct));
                WriteVerbose($"Added tag '{Tag}' to {Path}");
            }
            catch (Exception ex)
            {
                WriteError(new ErrorRecord(ex, "AddTagFailed",
                    ErrorCategory.WriteError, Path));
            }
        }
    }

    /// <summary>Removes a tag from a Dropbox file or folder.</summary>
    [Cmdlet(VerbsCommon.Remove, "DropboxTag", SupportsShouldProcess = true)]
    public class RemoveDropboxTagCommand : DropboxCmdletBase
    {
        [Parameter(Mandatory = true, Position = 0, ValueFromPipeline = true,
            ValueFromPipelineByPropertyName = true)]
        [Alias("FullName")]
        public string Path { get; set; } = string.Empty;

        [Parameter(Mandatory = true, Position = 1)]
        public string Tag { get; set; } = string.Empty;

        protected override void ProcessRecord()
        {
            try
            {
                if (ShouldProcess($"Tag '{Tag}' from {Path}", "Remove"))
                {
                    var service = GetService();
                    Run(ct => service.RemoveTagAsync(Path, Tag, cancellationToken: ct));
                    WriteVerbose($"Removed tag '{Tag}' from {Path}");
                }
            }
            catch (Exception ex)
            {
                WriteError(new ErrorRecord(ex, "RemoveTagFailed",
                    ErrorCategory.WriteError, Path));
            }
        }
    }

    /// <summary>Gets tags for Dropbox files or folders.</summary>
    [Cmdlet(VerbsCommon.Get, "DropboxTag")]
    [OutputType(typeof(DropboxTag))]
    public class GetDropboxTagCommand : DropboxCmdletBase
    {
        [Parameter(Mandatory = true, Position = 0, ValueFromPipeline = true,
            ValueFromPipelineByPropertyName = true)]
        [Alias("FullName")]
        public string[] Path { get; set; } = Array.Empty<string>();

        protected override void ProcessRecord()
        {
            try
            {
                var service = GetService();
                var tags = Run(ct => service.GetTagsAsync(Path, cancellationToken: ct));
                foreach (var tag in tags)
                {
                    WriteObject(tag);
                }
            }
            catch (Exception ex)
            {
                WriteError(new ErrorRecord(ex, "GetTagFailed",
                    ErrorCategory.ReadError, null));
            }
        }
    }
}