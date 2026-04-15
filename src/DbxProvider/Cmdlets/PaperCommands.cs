using System;
using System.Management.Automation;
using System.Text;

namespace DbxProvider.Cmdlets
{
    /// <summary>Creates a Paper document in Dropbox.</summary>
    [Cmdlet(VerbsCommon.New, "DropboxPaper")]
    [OutputType(typeof(string))]
    public class NewDropboxPaperCommand : DropboxCmdletBase
    {
        [Parameter(Mandatory = true, Position = 0)]
        public string Path { get; set; } = string.Empty;

        [Parameter(Mandatory = true, Position = 1, ValueFromPipeline = true)]
        public string Content { get; set; } = string.Empty;

        [Parameter]
        [ValidateSet("html", "markdown", "plain_text")]
        public string ImportFormat { get; set; } = "markdown";

        protected override void ProcessRecord()
        {
            try
            {
                var service = GetService();
                var bytes = Encoding.UTF8.GetBytes(Content);
                var url = service.CreatePaperDocAsync(Path, bytes, ImportFormat).GetAwaiter().GetResult();
                WriteObject(url);
                WriteVerbose($"Created Paper doc at {Path}: {url}");
            }
            catch (Exception ex)
            {
                WriteError(new ErrorRecord(ex, "CreatePaperFailed",
                    ErrorCategory.WriteError, Path));
            }
        }
    }

    /// <summary>Updates a Paper document in Dropbox.</summary>
    [Cmdlet(VerbsCommon.Set, "DropboxPaper")]
    [OutputType(typeof(string))]
    public class SetDropboxPaperCommand : DropboxCmdletBase
    {
        [Parameter(Mandatory = true, Position = 0)]
        public string Path { get; set; } = string.Empty;

        [Parameter(Mandatory = true, Position = 1, ValueFromPipeline = true)]
        public string Content { get; set; } = string.Empty;

        [Parameter]
        [ValidateSet("html", "markdown", "plain_text")]
        public string ImportFormat { get; set; } = "markdown";

        [Parameter]
        [ValidateSet("overwrite", "prepend", "append")]
        public string UpdatePolicy { get; set; } = "overwrite";

        protected override void ProcessRecord()
        {
            try
            {
                var service = GetService();
                var bytes = Encoding.UTF8.GetBytes(Content);
                var url = service.UpdatePaperDocAsync(Path, bytes, ImportFormat, UpdatePolicy)
                    .GetAwaiter().GetResult();
                WriteObject(url);
                WriteVerbose($"Updated Paper doc at {Path}: {url}");
            }
            catch (Exception ex)
            {
                WriteError(new ErrorRecord(ex, "UpdatePaperFailed",
                    ErrorCategory.WriteError, Path));
            }
        }
    }
}