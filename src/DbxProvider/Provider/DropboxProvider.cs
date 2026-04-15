using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Management.Automation;
using System.Management.Automation.Provider;
using DbxProvider.Models;
using DbxProvider.Services;

namespace DbxProvider.Provider
{
    /// <summary>
    /// PowerShell navigation provider for Dropbox.
    /// Supports file system-like operations: cd, dir, cat, copy, move, del, mkdir, etc.
    /// </summary>
    [CmdletProvider("Dropbox", ProviderCapabilities.ShouldProcess)]
    public class DropboxProvider : NavigationCmdletProvider, IContentCmdletProvider, IPropertyCmdletProvider
    {
        #region Drive Management

        protected override PSDriveInfo NewDrive(PSDriveInfo drive)
        {
            if (drive == null)
            {
                WriteError(new ErrorRecord(
                    new ArgumentNullException(nameof(drive)),
                    "NullDrive", ErrorCategory.InvalidArgument, null));
                return null!;
            }

            var dynParams = DynamicParameters as DropboxDriveParameters;
            if (dynParams?.AccessToken == null)
            {
                WriteError(new ErrorRecord(
                    new ArgumentException("AccessToken is required. Use -AccessToken parameter."),
                    "MissingAccessToken", ErrorCategory.InvalidArgument, drive));
                return null!;
            }

            try
            {
                var driveInfo = new DropboxDriveInfo(drive, dynParams.AccessToken);
                // Verify connection
                driveInfo.Service.GetCurrentAccountAsync().GetAwaiter().GetResult();
                return driveInfo;
            }
            catch (Exception ex)
            {
                WriteError(new ErrorRecord(ex, "DriveConnectionFailed",
                    ErrorCategory.ConnectionError, drive));
                return null!;
            }
        }

        protected override object NewDriveDynamicParameters()
        {
            return new DropboxDriveParameters();
        }

        protected override PSDriveInfo RemoveDrive(PSDriveInfo drive)
        {
            if (drive is DropboxDriveInfo dbxDrive)
            {
                dbxDrive.Service.Dispose();
            }
            return drive;
        }

        #endregion

        #region Path Helpers

        private DropboxServiceClient GetService()
        {
            if (PSDriveInfo is DropboxDriveInfo dbx)
                return dbx.Service;

            throw new InvalidOperationException(
                "No Dropbox drive found. Use New-PSDrive or Connect-Dropbox first.");
        }

        private static string ToDropboxPath(string providerPath)
        {
            return DropboxServiceClient.NormalizePath(providerPath);
        }

        protected override bool IsValidPath(string path)
        {
            return path != null;
        }

        protected override string MakePath(string parent, string child)
        {
            if (string.IsNullOrEmpty(parent)) return child;
            if (string.IsNullOrEmpty(child)) return parent;
            parent = parent.TrimEnd('\\', '/');
            child = child.TrimStart('\\', '/');
            return parent + "\\" + child;
        }

        protected override string GetParentPath(string path, string root)
        {
            if (string.IsNullOrEmpty(path)) return string.Empty;
            path = path.TrimEnd('\\', '/');
            var lastSep = path.LastIndexOfAny(new[] { '\\', '/' });
            if (lastSep <= 0) return string.Empty;
            return path.Substring(0, lastSep);
        }

        protected override string GetChildName(string path)
        {
            if (string.IsNullOrEmpty(path)) return string.Empty;
            path = path.TrimEnd('\\', '/');
            var lastSep = path.LastIndexOfAny(new[] { '\\', '/' });
            return lastSep < 0 ? path : path.Substring(lastSep + 1);
        }

        protected override string NormalizeRelativePath(string path, string basePath)
        {
            return base.NormalizeRelativePath(path, basePath);
        }

        #endregion

        #region Item Operations (Get-Item, Test-Path)

        protected override bool ItemExists(string path)
        {
            if (string.IsNullOrEmpty(path) || path == "\\" || path == "/")
                return true;

            try
            {
                var service = GetService();
                return service.ItemExistsAsync(path).GetAwaiter().GetResult();
            }
            catch
            {
                return false;
            }
        }

        protected override void GetItem(string path)
        {
            try
            {
                var service = GetService();
                var item = service.GetMetadataAsync(path).GetAwaiter().GetResult();
                WriteItemObject(item, item.Path, item.IsFolder);
            }
            catch (Exception ex)
            {
                WriteError(new ErrorRecord(ex, "GetItemFailed",
                    ErrorCategory.ReadError, path));
            }
        }

        protected override bool IsItemContainer(string path)
        {
            if (string.IsNullOrEmpty(path) || path == "\\" || path == "/")
                return true;

            try
            {
                var service = GetService();
                var item = service.GetMetadataAsync(path).GetAwaiter().GetResult();
                return item.IsFolder;
            }
            catch
            {
                return false;
            }
        }

        #endregion

        #region Container Operations (Get-ChildItem)

        protected override bool HasChildItems(string path)
        {
            try
            {
                var service = GetService();
                var items = service.ListFolderAsync(path).GetAwaiter().GetResult();
                return items.Count > 0;
            }
            catch
            {
                return false;
            }
        }

        protected override void GetChildItems(string path, bool recurse)
        {
            try
            {
                var service = GetService();
                var items = service.ListFolderAsync(path, recursive: recurse).GetAwaiter().GetResult();
                foreach (var item in items.OrderBy(i => !i.IsFolder).ThenBy(i => i.Name))
                {
                    var providerPath = item.Path.Replace('/', '\\').TrimStart('\\');
                    WriteItemObject(item, providerPath, item.IsFolder);
                }
            }
            catch (Exception ex)
            {
                WriteError(new ErrorRecord(ex, "GetChildItemsFailed",
                    ErrorCategory.ReadError, path));
            }
        }

        protected override void GetChildNames(string path, ReturnContainers returnContainers)
        {
            try
            {
                var service = GetService();
                var items = service.ListFolderAsync(path).GetAwaiter().GetResult();
                foreach (var item in items.OrderBy(i => !i.IsFolder).ThenBy(i => i.Name))
                {
                    WriteItemObject(item.Name, item.Path.Replace('/', '\\').TrimStart('\\'), item.IsFolder);
                }
            }
            catch (Exception ex)
            {
                WriteError(new ErrorRecord(ex, "GetChildNamesFailed",
                    ErrorCategory.ReadError, path));
            }
        }

        #endregion

        #region New / Remove Item

        protected override void NewItem(string path, string itemTypeName, object newItemValue)
        {
            try
            {
                var service = GetService();

                if (string.Equals(itemTypeName, "Directory", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(itemTypeName, "Folder", StringComparison.OrdinalIgnoreCase))
                {
                    if (ShouldProcess(path, "Create folder"))
                    {
                        var item = service.CreateFolderAsync(path).GetAwaiter().GetResult();
                        WriteItemObject(item, item.Path.Replace('/', '\\').TrimStart('\\'), true);
                    }
                }
                else
                {
                    if (ShouldProcess(path, "Create file"))
                    {
                        var content = newItemValue?.ToString() ?? "";
                        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(content));
                        var item = service.UploadAsync(path, stream).GetAwaiter().GetResult();
                        WriteItemObject(item, item.Path.Replace('/', '\\').TrimStart('\\'), false);
                    }
                }
            }
            catch (Exception ex)
            {
                WriteError(new ErrorRecord(ex, "NewItemFailed",
                    ErrorCategory.WriteError, path));
            }
        }

        protected override void RemoveItem(string path, bool recurse)
        {
            try
            {
                var service = GetService();
                bool permanent = false;

                if (Force)
                {
                    permanent = true;
                }

                if (ShouldProcess(path, permanent ? "Permanently delete" : "Delete"))
                {
                    service.DeleteAsync(path, permanent).GetAwaiter().GetResult();
                }
            }
            catch (Exception ex)
            {
                WriteError(new ErrorRecord(ex, "RemoveItemFailed",
                    ErrorCategory.WriteError, path));
            }
        }

        #endregion

        #region Copy / Move Item

        protected override void CopyItem(string path, string copyPath, bool recurse)
        {
            try
            {
                var service = GetService();
                if (ShouldProcess($"{path} -> {copyPath}", "Copy"))
                {
                    var item = service.CopyAsync(path, copyPath).GetAwaiter().GetResult();
                    WriteItemObject(item, item.Path.Replace('/', '\\').TrimStart('\\'), item.IsFolder);
                }
            }
            catch (Exception ex)
            {
                WriteError(new ErrorRecord(ex, "CopyItemFailed",
                    ErrorCategory.WriteError, path));
            }
        }

        protected override void MoveItem(string path, string destination)
        {
            try
            {
                var service = GetService();
                if (ShouldProcess($"{path} -> {destination}", "Move"))
                {
                    var item = service.MoveAsync(path, destination).GetAwaiter().GetResult();
                    WriteItemObject(item, item.Path.Replace('/', '\\').TrimStart('\\'), item.IsFolder);
                }
            }
            catch (Exception ex)
            {
                WriteError(new ErrorRecord(ex, "MoveItemFailed",
                    ErrorCategory.WriteError, path));
            }
        }

        protected override void RenameItem(string path, string newName)
        {
            try
            {
                var service = GetService();
                var parentPath = GetParentPath(path, null!);
                var newPath = MakePath(parentPath, newName);
                if (ShouldProcess($"{path} -> {newPath}", "Rename"))
                {
                    var item = service.MoveAsync(path, newPath).GetAwaiter().GetResult();
                    WriteItemObject(item, item.Path.Replace('/', '\\').TrimStart('\\'), item.IsFolder);
                }
            }
            catch (Exception ex)
            {
                WriteError(new ErrorRecord(ex, "RenameItemFailed",
                    ErrorCategory.WriteError, path));
            }
        }

        #endregion

        #region IContentCmdletProvider (Get-Content, Set-Content, Clear-Content)

        public IContentReader GetContentReader(string path)
        {
            var service = GetService();
            bool raw = false;
            if (DynamicParameters is DropboxContentReaderDynamicParameters dynParams)
            {
                raw = dynParams.AsByteStream;
            }
            return new DropboxContentReader(service, path, raw);
        }

        public object GetContentReaderDynamicParameters(string path)
        {
            return new DropboxContentReaderDynamicParameters();
        }

        public IContentWriter GetContentWriter(string path)
        {
            var service = GetService();
            bool raw = false;
            if (DynamicParameters is DropboxContentWriterDynamicParameters dynParams)
            {
                raw = dynParams.AsByteStream;
            }
            return new DropboxContentWriter(service, path, raw);
        }

        public object GetContentWriterDynamicParameters(string path)
        {
            return new DropboxContentWriterDynamicParameters();
        }

        public void ClearContent(string path)
        {
            try
            {
                var service = GetService();
                if (ShouldProcess(path, "Clear content"))
                {
                    using var empty = new MemoryStream();
                    service.UploadAsync(path, empty).GetAwaiter().GetResult();
                }
            }
            catch (Exception ex)
            {
                WriteError(new ErrorRecord(ex, "ClearContentFailed",
                    ErrorCategory.WriteError, path));
            }
        }

        public object ClearContentDynamicParameters(string path)
        {
            return null!;
        }

        #endregion

        #region IPropertyCmdletProvider (Get-ItemProperty, Set-ItemProperty, Clear-ItemProperty)

        public void GetProperty(string path, Collection<string>? providerSpecificPickList)
        {
            try
            {
                var service = GetService();
                var item = service.GetMetadataAsync(path).GetAwaiter().GetResult();
                var pso = new PSObject();

                var properties = new (string Name, object? Value)[]
                {
                    ("Name", item.Name),
                    ("Path", item.Path),
                    ("Id", item.Id),
                    ("IsFolder", item.IsFolder),
                    ("Size", item.Size),
                    ("ServerModified", item.ServerModified),
                    ("ClientModified", item.ClientModified),
                    ("Rev", item.Rev),
                    ("ContentHash", item.ContentHash),
                    ("IsDeleted", item.IsDeleted),
                    ("SharedFolderId", item.SharedFolderId),
                    ("ParentSharedFolderId", item.ParentSharedFolderId),
                    ("HasExplicitSharedMembers", item.HasExplicitSharedMembers),
                    ("IsDownloadable", item.IsDownloadable),
                    ("DisplaySize", item.DisplaySize)
                };

                foreach (var (name, value) in properties)
                {
                    if (providerSpecificPickList == null || providerSpecificPickList.Count == 0 ||
                        providerSpecificPickList.Contains(name, StringComparer.OrdinalIgnoreCase))
                    {
                        pso.Properties.Add(new PSNoteProperty(name, value));
                    }
                }

                WritePropertyObject(pso, path);
            }
            catch (Exception ex)
            {
                WriteError(new ErrorRecord(ex, "GetPropertyFailed",
                    ErrorCategory.ReadError, path));
            }
        }

        public object? GetPropertyDynamicParameters(string path, Collection<string>? providerSpecificPickList)
        {
            return null!;
        }

        public void SetProperty(string path, PSObject propertyValue)
        {
            // Dropbox API has limited property-setting support.
            // ClientModified can be set during upload.
            WriteWarning("Dropbox does not support arbitrary property modification. " +
                "Use Set-Content to update file content, or use the sharing cmdlets for sharing properties.");
        }

        public object SetPropertyDynamicParameters(string path, PSObject propertyValue)
        {
            return null!;
        }

        public void ClearProperty(string path, Collection<string>? propertyToClear)
        {
            WriteWarning("Dropbox does not support clearing individual properties.");
        }

        public object? ClearPropertyDynamicParameters(string path, Collection<string>? propertyToClear)
        {
            return null!;
        }

        #endregion
    }

    public class DropboxContentReaderDynamicParameters
    {
        [Parameter]
        public SwitchParameter AsByteStream { get; set; }
    }

    public class DropboxContentWriterDynamicParameters
    {
        [Parameter]
        public SwitchParameter AsByteStream { get; set; }
    }
}