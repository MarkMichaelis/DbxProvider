using System;
using System.Management.Automation;
using DbxProvider.Models;

namespace DbxProvider.Cmdlets
{
    /// <summary>Gets the current Dropbox account information.</summary>
    [Cmdlet(VerbsCommon.Get, "DropboxAccount")]
    [OutputType(typeof(DropboxAccount))]
    public class GetDropboxAccountCommand : DropboxCmdletBase
    {
        [Parameter(Position = 0)]
        public string? AccountId { get; set; }

        protected override void ProcessRecord()
        {
            try
            {
                var service = GetService();

                if (!string.IsNullOrEmpty(AccountId))
                {
                    var account = Run(ct => service.GetAccountAsync(AccountId, cancellationToken: ct));
                    WriteObject(account);
                }
                else
                {
                    var account = Run(ct => service.GetCurrentAccountAsync(cancellationToken: ct));
                    WriteObject(account);
                }
            }
            catch (Exception ex)
            {
                WriteError(new ErrorRecord(ex, "GetAccountFailed",
                    ErrorCategory.ReadError, AccountId));
            }
        }
    }

    /// <summary>Gets Dropbox space usage information.</summary>
    [Cmdlet(VerbsCommon.Get, "DropboxSpaceUsage")]
    [OutputType(typeof(DropboxSpaceUsage))]
    public class GetDropboxSpaceUsageCommand : DropboxCmdletBase
    {
        protected override void ProcessRecord()
        {
            try
            {
                var service = GetService();
                var usage = Run(ct => service.GetSpaceUsageAsync(cancellationToken: ct));
                WriteObject(usage);
            }
            catch (Exception ex)
            {
                WriteError(new ErrorRecord(ex, "GetSpaceUsageFailed",
                    ErrorCategory.ReadError, null));
            }
        }
    }
}