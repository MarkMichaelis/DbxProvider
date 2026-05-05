using System;
using System.Management.Automation;

namespace DbxProvider.Cmdlets
{
    /// <summary>Gets a temporary download link for a Dropbox file.</summary>
    [Cmdlet(VerbsCommon.Get, "DropboxTemporaryLink")]
    [OutputType(typeof(string))]
    public class GetDropboxTemporaryLinkCommand : DropboxCmdletBase
    {
        [Parameter(Mandatory = true, Position = 0, ValueFromPipeline = true,
            ValueFromPipelineByPropertyName = true)]
        [Alias("FullName")]
        public string Path { get; set; } = string.Empty;

        protected override void ProcessRecord()
        {
            try
            {
                var service = GetService();
                var link = Run(ct => service.GetTemporaryLinkAsync(Path, cancellationToken: ct));
                WriteObject(link);
            }
            catch (Exception ex)
            {
                WriteError(new ErrorRecord(ex, "GetTemporaryLinkFailed",
                    ErrorCategory.ReadError, Path));
            }
        }
    }

    /// <summary>Saves a URL to Dropbox (downloads the URL content to the specified path).</summary>
    [Cmdlet(VerbsData.Save, "DropboxUrl")]
    [OutputType(typeof(string))]
    public class SaveDropboxUrlCommand : DropboxCmdletBase
    {
        [Parameter(Mandatory = true, Position = 0)]
        public string DropboxPath { get; set; } = string.Empty;

        [Parameter(Mandatory = true, Position = 1)]
        public string Url { get; set; } = string.Empty;

        protected override void ProcessRecord()
        {
            try
            {
                var service = GetService();
                var result = Run(ct => service.SaveUrlAsync(DropboxPath, Url, cancellationToken: ct));
                WriteObject(result);
                WriteVerbose($"Save URL job: {result}");
            }
            catch (Exception ex)
            {
                WriteError(new ErrorRecord(ex, "SaveUrlFailed",
                    ErrorCategory.WriteError, Url));
            }
        }
    }
}