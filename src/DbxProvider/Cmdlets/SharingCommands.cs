using System;
using System.Management.Automation;
using DbxProvider.Models;

namespace DbxProvider.Cmdlets
{
    /// <summary>Shares a Dropbox folder.</summary>
    [Cmdlet(VerbsCommon.Add, "DropboxSharedFolder", DefaultParameterSetName = "Share")]
    [OutputType(typeof(string))]
    public class ShareDropboxFolderCommand : DropboxCmdletBase
    {
        [Parameter(Mandatory = true, Position = 0)]
        public string Path { get; set; } = string.Empty;

        protected override void ProcessRecord()
        {
            try
            {
                var service = GetService();
                var result = service.ShareFolderAsync(Path).GetAwaiter().GetResult();
                WriteObject(result);
                WriteVerbose($"Shared folder {Path}, ID: {result}");
            }
            catch (Exception ex)
            {
                WriteError(new ErrorRecord(ex, "ShareFolderFailed",
                    ErrorCategory.WriteError, Path));
            }
        }
    }

    /// <summary>Unshares a Dropbox folder.</summary>
    [Cmdlet(VerbsCommon.Remove, "DropboxSharedFolder", SupportsShouldProcess = true)]
    public class UnshareDropboxFolderCommand : DropboxCmdletBase
    {
        [Parameter(Mandatory = true, Position = 0)]
        public string SharedFolderId { get; set; } = string.Empty;

        [Parameter]
        public SwitchParameter LeaveACopy { get; set; }

        protected override void ProcessRecord()
        {
            try
            {
                if (ShouldProcess(SharedFolderId, "Unshare folder"))
                {
                    var service = GetService();
                    service.UnshareFolderAsync(SharedFolderId, LeaveACopy).GetAwaiter().GetResult();
                    WriteVerbose($"Unshared folder {SharedFolderId}");
                }
            }
            catch (Exception ex)
            {
                WriteError(new ErrorRecord(ex, "UnshareFolderFailed",
                    ErrorCategory.WriteError, SharedFolderId));
            }
        }
    }

    /// <summary>Lists shared folders.</summary>
    [Cmdlet(VerbsCommon.Get, "DropboxSharedFolder")]
    [OutputType(typeof(DropboxSharedFolder))]
    public class GetDropboxSharedFolderCommand : DropboxCmdletBase
    {
        [Parameter(Position = 0)]
        public string? SharedFolderId { get; set; }

        protected override void ProcessRecord()
        {
            try
            {
                var service = GetService();

                if (!string.IsNullOrEmpty(SharedFolderId))
                {
                    var folder = service.GetSharedFolderMetadataAsync(SharedFolderId)
                        .GetAwaiter().GetResult();
                    WriteObject(folder);
                }
                else
                {
                    var folders = service.ListSharedFoldersAsync().GetAwaiter().GetResult();
                    foreach (var folder in folders)
                    {
                        WriteObject(folder);
                    }
                }
            }
            catch (Exception ex)
            {
                WriteError(new ErrorRecord(ex, "GetSharedFolderFailed",
                    ErrorCategory.ReadError, SharedFolderId));
            }
        }
    }

    /// <summary>Adds a member to a shared file or folder.</summary>
    [Cmdlet(VerbsCommon.Add, "DropboxMember")]
    public class AddDropboxMemberCommand : DropboxCmdletBase
    {
        [Parameter(Mandatory = true, Position = 0, ParameterSetName = "Folder")]
        public string? SharedFolderId { get; set; }

        [Parameter(Mandatory = true, Position = 0, ParameterSetName = "File")]
        public string? FilePath { get; set; }

        [Parameter(Mandatory = true, Position = 1)]
        public string Email { get; set; } = string.Empty;

        [Parameter]
        [ValidateSet("editor", "viewer", "viewer_no_comment")]
        public string AccessLevel { get; set; } = "viewer";

        protected override void ProcessRecord()
        {
            try
            {
                var service = GetService();

                if (!string.IsNullOrEmpty(SharedFolderId))
                {
                    service.AddFolderMemberAsync(SharedFolderId, Email, AccessLevel)
                        .GetAwaiter().GetResult();
                    WriteVerbose($"Added {Email} to shared folder {SharedFolderId} as {AccessLevel}");
                }
                else if (!string.IsNullOrEmpty(FilePath))
                {
                    service.AddFileMemberAsync(FilePath, Email, AccessLevel)
                        .GetAwaiter().GetResult();
                    WriteVerbose($"Added {Email} to file {FilePath} as {AccessLevel}");
                }
            }
            catch (Exception ex)
            {
                WriteError(new ErrorRecord(ex, "AddMemberFailed",
                    ErrorCategory.WriteError, Email));
            }
        }
    }

    /// <summary>Removes a member from a shared file or folder.</summary>
    [Cmdlet(VerbsCommon.Remove, "DropboxMember", SupportsShouldProcess = true)]
    public class RemoveDropboxMemberCommand : DropboxCmdletBase
    {
        [Parameter(Mandatory = true, Position = 0, ParameterSetName = "Folder")]
        public string? SharedFolderId { get; set; }

        [Parameter(Mandatory = true, Position = 0, ParameterSetName = "File")]
        public string? FilePath { get; set; }

        [Parameter(Mandatory = true, Position = 1)]
        public string Email { get; set; } = string.Empty;

        protected override void ProcessRecord()
        {
            try
            {
                var target = SharedFolderId ?? FilePath ?? "";
                if (ShouldProcess($"{Email} from {target}", "Remove member"))
                {
                    var service = GetService();

                    if (!string.IsNullOrEmpty(SharedFolderId))
                    {
                        service.RemoveFolderMemberAsync(SharedFolderId, Email)
                            .GetAwaiter().GetResult();
                        WriteVerbose($"Removed {Email} from shared folder {SharedFolderId}");
                    }
                    else if (!string.IsNullOrEmpty(FilePath))
                    {
                        service.RemoveFileMemberAsync(FilePath, Email)
                            .GetAwaiter().GetResult();
                        WriteVerbose($"Removed {Email} from file {FilePath}");
                    }
                }
            }
            catch (Exception ex)
            {
                WriteError(new ErrorRecord(ex, "RemoveMemberFailed",
                    ErrorCategory.WriteError, Email));
            }
        }
    }

    /// <summary>Lists members of a shared file or folder.</summary>
    [Cmdlet(VerbsCommon.Get, "DropboxMember")]
    [OutputType(typeof(DropboxMember))]
    public class GetDropboxMemberCommand : DropboxCmdletBase
    {
        [Parameter(Mandatory = true, Position = 0, ParameterSetName = "Folder")]
        public string? SharedFolderId { get; set; }

        [Parameter(Mandatory = true, Position = 0, ParameterSetName = "File")]
        public string? FilePath { get; set; }

        protected override void ProcessRecord()
        {
            try
            {
                var service = GetService();

                if (!string.IsNullOrEmpty(SharedFolderId))
                {
                    var members = service.ListFolderMembersAsync(SharedFolderId)
                        .GetAwaiter().GetResult();
                    foreach (var m in members) WriteObject(m);
                }
                else if (!string.IsNullOrEmpty(FilePath))
                {
                    var members = service.ListFileMembersAsync(FilePath)
                        .GetAwaiter().GetResult();
                    foreach (var m in members) WriteObject(m);
                }
            }
            catch (Exception ex)
            {
                WriteError(new ErrorRecord(ex, "GetMemberFailed",
                    ErrorCategory.ReadError, SharedFolderId ?? FilePath));
            }
        }
    }
}