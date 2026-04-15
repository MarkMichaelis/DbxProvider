using System;
using System.Linq;
using System.Management.Automation;
using DbxProvider.Models;
using DbxProvider.Provider;
using DbxProvider.Services;

namespace DbxProvider.Cmdlets
{
    /// <summary>Base class for cmdlets that need a Dropbox service client.</summary>
    public abstract class DropboxCmdletBase : PSCmdlet
    {
        [Parameter]
        public string DriveName { get; set; } = "Dbx";

        protected DropboxServiceClient GetService()
        {
            var drive = SessionState.Drive.Get(DriveName);
            if (drive is DropboxDriveInfo dbxDrive)
                return dbxDrive.Service;

            throw new InvalidOperationException(
                $"Drive '{DriveName}:' is not a Dropbox drive. Use Connect-Dropbox first.");
        }
    }

    /// <summary>Searches for files and folders in Dropbox.</summary>
    [Cmdlet(VerbsCommon.Search, "Dropbox")]
    [OutputType(typeof(DropboxSearchResult))]
    public class SearchDropboxCommand : DropboxCmdletBase
    {
        [Parameter(Mandatory = true, Position = 0)]
        public string Query { get; set; } = string.Empty;

        [Parameter]
        public string Path { get; set; } = "";

        [Parameter]
        public int MaxResults { get; set; } = 100;

        [Parameter]
        public SwitchParameter IncludeHighlights { get; set; }

        protected override void ProcessRecord()
        {
            try
            {
                var service = GetService();
                var results = service.SearchAsync(Query, Path, MaxResults,
                    IncludeHighlights).GetAwaiter().GetResult();

                foreach (var result in results)
                {
                    WriteObject(result);
                }

                WriteVerbose($"Found {results.Count} results for '{Query}'");
            }
            catch (Exception ex)
            {
                WriteError(new ErrorRecord(ex, "SearchFailed",
                    ErrorCategory.ReadError, Query));
            }
        }
    }
}