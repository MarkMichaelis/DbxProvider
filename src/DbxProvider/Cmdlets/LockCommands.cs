using System;
using System.Management.Automation;
using DbxProvider.Models;

namespace DbxProvider.Cmdlets
{
    /// <summary>Locks one or more files in Dropbox.</summary>
    [Cmdlet(VerbsCommon.Lock, "DropboxFile")]
    [OutputType(typeof(DropboxItem))]
    public class LockDropboxFileCommand : DropboxCmdletBase
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
                var items = service.LockFilesAsync(Path).GetAwaiter().GetResult();
                foreach (var item in items)
                {
                    WriteObject(item);
                }
            }
            catch (Exception ex)
            {
                WriteError(new ErrorRecord(ex, "LockFileFailed",
                    ErrorCategory.WriteError, null));
            }
        }
    }

    /// <summary>Unlocks one or more files in Dropbox.</summary>
    [Cmdlet(VerbsCommon.Unlock, "DropboxFile")]
    [OutputType(typeof(DropboxItem))]
    public class UnlockDropboxFileCommand : DropboxCmdletBase
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
                var items = service.UnlockFilesAsync(Path).GetAwaiter().GetResult();
                foreach (var item in items)
                {
                    WriteObject(item);
                }
            }
            catch (Exception ex)
            {
                WriteError(new ErrorRecord(ex, "UnlockFileFailed",
                    ErrorCategory.WriteError, null));
            }
        }
    }

    /// <summary>Gets lock status for files in Dropbox.</summary>
    [Cmdlet(VerbsCommon.Get, "DropboxFileLock")]
    [OutputType(typeof(DropboxItem))]
    public class GetDropboxFileLockCommand : DropboxCmdletBase
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
                var items = service.GetFileLocksAsync(Path).GetAwaiter().GetResult();
                foreach (var item in items)
                {
                    WriteObject(item);
                }
            }
            catch (Exception ex)
            {
                WriteError(new ErrorRecord(ex, "GetFileLockFailed",
                    ErrorCategory.ReadError, null));
            }
        }
    }
}