using System;
using System.Management.Automation;
using DbxProvider.Models;

namespace DbxProvider.Cmdlets
{
    /// <summary>Creates a shared link for a Dropbox file or folder.</summary>
    [Cmdlet(VerbsCommon.New, "DropboxSharedLink")]
    [OutputType(typeof(DropboxSharedLink))]
    public class NewDropboxSharedLinkCommand : DropboxCmdletBase
    {
        [Parameter(Mandatory = true, Position = 0, ValueFromPipeline = true,
            ValueFromPipelineByPropertyName = true)]
        [Alias("FullName")]
        public string Path { get; set; } = string.Empty;

        [Parameter]
        [ValidateSet("public", "team_only", "password")]
        public string? Visibility { get; set; }

        [Parameter]
        public DateTime? Expires { get; set; }

        protected override void ProcessRecord()
        {
            try
            {
                var service = GetService();
                var link = service.CreateSharedLinkAsync(Path, Visibility, Expires)
                    .GetAwaiter().GetResult();
                WriteObject(link);
            }
            catch (Exception ex)
            {
                WriteError(new ErrorRecord(ex, "CreateSharedLinkFailed",
                    ErrorCategory.WriteError, Path));
            }
        }
    }

    /// <summary>Lists shared links for a Dropbox path or all shared links.</summary>
    [Cmdlet(VerbsCommon.Get, "DropboxSharedLink")]
    [OutputType(typeof(DropboxSharedLink))]
    public class GetDropboxSharedLinkCommand : DropboxCmdletBase
    {
        [Parameter(Position = 0, ValueFromPipeline = true,
            ValueFromPipelineByPropertyName = true)]
        [Alias("FullName")]
        public string? Path { get; set; }

        [Parameter(ParameterSetName = "ByUrl")]
        public string? Url { get; set; }

        protected override void ProcessRecord()
        {
            try
            {
                var service = GetService();

                if (!string.IsNullOrEmpty(Url))
                {
                    var link = service.GetSharedLinkMetadataAsync(Url).GetAwaiter().GetResult();
                    WriteObject(link);
                }
                else
                {
                    var links = service.ListSharedLinksAsync(Path).GetAwaiter().GetResult();
                    foreach (var link in links)
                    {
                        WriteObject(link);
                    }
                }
            }
            catch (Exception ex)
            {
                WriteError(new ErrorRecord(ex, "GetSharedLinkFailed",
                    ErrorCategory.ReadError, Path));
            }
        }
    }

    /// <summary>Revokes a shared link.</summary>
    [Cmdlet(VerbsCommon.Remove, "DropboxSharedLink", SupportsShouldProcess = true)]
    public class RemoveDropboxSharedLinkCommand : DropboxCmdletBase
    {
        [Parameter(Mandatory = true, Position = 0, ValueFromPipeline = true,
            ValueFromPipelineByPropertyName = true)]
        public string Url { get; set; } = string.Empty;

        protected override void ProcessRecord()
        {
            try
            {
                if (ShouldProcess(Url, "Revoke shared link"))
                {
                    var service = GetService();
                    service.RevokeSharedLinkAsync(Url).GetAwaiter().GetResult();
                    WriteVerbose($"Revoked shared link: {Url}");
                }
            }
            catch (Exception ex)
            {
                WriteError(new ErrorRecord(ex, "RevokeSharedLinkFailed",
                    ErrorCategory.WriteError, Url));
            }
        }
    }
}