using System;
using System.Management.Automation;
using IntelliTect.Dropbox;

namespace DbxProvider.Cmdlets
{
    /// <summary>Gets file revisions from Dropbox.</summary>
    [Cmdlet(VerbsCommon.Get, "DropboxRevision")]
    [OutputType(typeof(DropboxRevision))]
    public class GetDropboxRevisionCommand : DropboxCmdletBase
    {
        [Parameter(Mandatory = true, Position = 0, ValueFromPipeline = true,
            ValueFromPipelineByPropertyName = true)]
        [Alias("FullName")]
        public string Path { get; set; } = string.Empty;

        [Parameter]
        public int Limit { get; set; } = 10;

        protected override void ProcessRecord()
        {
            try
            {
                var service = GetService();
                var revisions = Run(ct => service.ListRevisionsAsync(Path, Limit, cancellationToken: ct));
                foreach (var rev in revisions)
                {
                    WriteObject(rev);
                }
            }
            catch (Exception ex)
            {
                WriteError(new ErrorRecord(ex, "GetRevisionFailed",
                    ErrorCategory.ReadError, Path));
            }
        }
    }

    /// <summary>Restores a file to a previous revision in Dropbox.</summary>
    [Cmdlet(VerbsData.Restore, "DropboxRevision", SupportsShouldProcess = true)]
    [OutputType(typeof(DropboxItem))]
    public class RestoreDropboxRevisionCommand : DropboxCmdletBase
    {
        [Parameter(Mandatory = true, Position = 0)]
        public string Path { get; set; } = string.Empty;

        [Parameter(Mandatory = true, Position = 1)]
        public string Rev { get; set; } = string.Empty;

        protected override void ProcessRecord()
        {
            try
            {
                if (ShouldProcess($"{Path} to revision {Rev}", "Restore"))
                {
                    var service = GetService();
                    var item = Run(ct => service.RestoreAsync(Path, Rev, cancellationToken: ct));
                    WriteObject(item);
                }
            }
            catch (Exception ex)
            {
                WriteError(new ErrorRecord(ex, "RestoreRevisionFailed",
                    ErrorCategory.WriteError, Path));
            }
        }
    }
}