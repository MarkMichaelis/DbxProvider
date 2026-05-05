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

            try
            {
                DropboxDriveInfo driveInfo;
                if (drive is DropboxDriveInfo existing && existing.Service != null)
                {
                    driveInfo = existing;
                }
                else
                {
                    var dynParams = DynamicParameters as DropboxDriveParameters;
                    if (string.IsNullOrEmpty(dynParams?.AccessToken))
                    {
                        WriteError(new ErrorRecord(
                            new ArgumentException("AccessToken is required. Use -AccessToken parameter."),
                            "MissingAccessToken", ErrorCategory.InvalidArgument, drive));
                        return null!;
                    }
                    driveInfo = new DropboxDriveInfo(drive, dynParams.AccessToken);
                }

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

                // Multi-segment wildcard (e.g. Dbx:\**\foo.docx) — resolve via search_v2.
                var noSearch = GetNoSearchParams()?.NoSearch.IsPresent == true;
                if (!noSearch && TrySplitWildcardPath(path, out var scope, out var pattern))
                {
                    // If only the leaf has a wildcard, fall through to default
                    // existence check (PowerShell typically resolves leaf wildcards
                    // via GetChildItems anyway).
                    var segments = path.Replace('/', '\\').Split('\\');
                    int wildcardSegments = segments.Count(s => WildcardPattern.ContainsWildcardCharacters(s));
                    bool deepWildcard = wildcardSegments > 1
                        || (wildcardSegments == 1
                            && !WildcardPattern.ContainsWildcardCharacters(segments.Last()));
                    if (deepWildcard)
                    {
                        var found = service.SearchByFilenameAsync(pattern, scope, 1)
                            .GetAwaiter().GetResult();
                        return found.Count > 0;
                    }
                }

                return service.ItemExistsAsync(path).GetAwaiter().GetResult();
            }
            catch
            {
                return false;
            }
        }

        protected override object ItemExistsDynamicParameters(string path)
        {
            return new NoSearchDynamicParameters();
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

        /// <summary>
        /// Splits a provider path that may contain wildcards into the
        /// non-wildcard ancestor (used as the search scope) and the wildcard
        /// pattern (used to filter results). Returns false if the path has no
        /// wildcards.
        /// </summary>
        private static bool TrySplitWildcardPath(string path, out string scope, out string pattern)
        {
            scope = path ?? "";
            pattern = "";
            if (string.IsNullOrEmpty(path)) return false;

            var segments = path.Replace('/', '\\').Split('\\');
            int firstWildcard = -1;
            for (int i = 0; i < segments.Length; i++)
            {
                if (WildcardPattern.ContainsWildcardCharacters(segments[i]))
                {
                    firstWildcard = i;
                    break;
                }
            }
            if (firstWildcard < 0) return false;

            scope = string.Join("\\", segments.Take(firstWildcard));
            // Pattern uses only the LAST wildcard segment as the filename pattern.
            // Intermediate wildcards (Dbx:\**\file) are honored implicitly because
            // search_v2 is recursive within the scope.
            pattern = segments.Last();
            return true;
        }

        private NoSearchDynamicParameters? GetNoSearchParams()
        {
            return DynamicParameters as NoSearchDynamicParameters;
        }

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
                var noSearch = GetNoSearchParams()?.NoSearch.IsPresent == true;

                // Route to search_v2 when scope is a subtree AND a wildcard/filter
                // is present. Single-folder wildcards (e.g. `dir *.dbx`) keep the
                // list-based path: PowerShell already filters the leaf wildcard
                // client-side, and search would silently widen the scope.
                if (!noSearch)
                {
                    bool pathHasWildcard = TrySplitWildcardPath(path, out var scope, out var pathPattern);
                    string? filterPattern = !string.IsNullOrEmpty(Filter) ? Filter : null;

                    if (recurse && (pathHasWildcard || filterPattern != null))
                    {
                        var pattern = filterPattern ?? pathPattern;
                        var searchScope = pathHasWildcard ? scope : path;
                        WriteVerbose($"Get-ChildItem: routing to search_v2 (scope='{searchScope}', pattern='{pattern}')");
                        var found = service.SearchByFilenameAsync(pattern, searchScope, 1000)
                            .GetAwaiter().GetResult();
                        foreach (var item in found.OrderBy(i => !i.IsFolder).ThenBy(i => i.Name))
                        {
                            var providerPath = item.Path.Replace('/', '\\').TrimStart('\\');
                            WriteItemObject(item, providerPath, item.IsFolder);
                        }
                        return;
                    }

                    if (!recurse && pathHasWildcard && pathPattern != null
                        && pathPattern.Contains('*') == false && pathPattern.Contains('?') == false)
                    {
                        // No-op branch placeholder; PS handles single-folder wildcards.
                    }
                }

                WriteVerbose($"Get-ChildItem: routing to list_folder (path='{path}', recurse={recurse})");
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

        protected override object GetChildItemsDynamicParameters(string path, bool recurse)
        {
            return new NoSearchDynamicParameters();
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

    /// <summary>
    /// Dynamic parameter exposed by Get-ChildItem and Test-Path on the Dropbox
    /// provider. Use -NoSearch to force the list-based path (skipping
    /// search_v2). Useful right after uploads while the search index lags.
    /// </summary>
    public class NoSearchDynamicParameters
    {
        [Parameter]
        public SwitchParameter NoSearch { get; set; }
    }
}