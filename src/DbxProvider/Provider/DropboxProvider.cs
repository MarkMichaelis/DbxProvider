using System;
using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Management.Automation;
using System.Management.Automation.Provider;
using System.Threading;
using System.Threading.Tasks;
using IntelliTect.Dropbox;

namespace DbxProvider.Provider
{
    /// <summary>
    /// PowerShell navigation provider for Dropbox.
    /// Supports file system-like operations: cd, dir, cat, copy, move, del, mkdir, etc.
    /// </summary>
    [CmdletProvider("Dropbox", ProviderCapabilities.ShouldProcess | ProviderCapabilities.Filter)]
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

                Run(ct => driveInfo.Service.GetCurrentAccountAsync(cancellationToken: ct));
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
                try { dbxDrive.Cache?.Dispose(); } catch { }
                dbxDrive.Service.Dispose();
            }
            return drive;
        }

        #endregion

        #region Cancellation / rate-limit run helper

        private readonly ConcurrentQueue<Action> _pendingWrites = new();

        /// <summary>
        /// Runs an async Dropbox call from the synchronous provider context.
        /// Polls <see cref="CmdletProvider.Stopping"/> to honor Ctrl+C (the
        /// underlying HTTP call is left to drain) and pumps queued
        /// <c>WriteWarning</c>/<c>WriteVerbose</c> messages emitted by the
        /// rate-limit notifier on the pipeline thread.
        /// </summary>
        private T Run<T>(Func<CancellationToken, Task<T>> op)
        {
            using var cts = new CancellationTokenSource();
            WireRateLimitNotifier();
            var task = op(cts.Token);
            PumpUntil(task, cts);
            try { return task.GetAwaiter().GetResult(); }
            catch (OperationCanceledException) { throw new PipelineStoppedException(); }
        }

        private void Run(Func<CancellationToken, Task> op)
        {
            using var cts = new CancellationTokenSource();
            WireRateLimitNotifier();
            var task = op(cts.Token);
            PumpUntil(task, cts);
            try { task.GetAwaiter().GetResult(); }
            catch (OperationCanceledException) { throw new PipelineStoppedException(); }
        }

        private void WireRateLimitNotifier()
        {
            if (PSDriveInfo is DropboxDriveInfo dbx)
            {
                dbx.Service.SetRateLimitNotifier(new ProviderRateLimitNotifier(this));
            }
        }

        private void PumpUntil(Task task, CancellationTokenSource cts)
        {
            while (!task.IsCompleted)
            {
                if (Stopping)
                {
                    try { cts.Cancel(); } catch { }
                }
                while (_pendingWrites.TryDequeue(out var action))
                {
                    try { action(); } catch { /* best-effort UI write */ }
                }
                // Wake immediately when the call finishes, but cap the wait so
                // Stopping (Ctrl+C) and queued UI writes are still serviced ~20x/s
                // during a long call -- avoiding a fixed 50ms latency floor on every
                // fast provider operation (navigation, existence checks).
                Task.WaitAny(new[] { task }, 50);
            }
            while (_pendingWrites.TryDequeue(out var action))
            {
                try { action(); } catch { }
            }
        }

        internal void EnqueueWrite(Action action) => _pendingWrites.Enqueue(action);

        private sealed class ProviderRateLimitNotifier : IRateLimitNotifier
        {
            private readonly DropboxProvider _provider;
            public ProviderRateLimitNotifier(DropboxProvider provider) => _provider = provider;

            public void OnRateLimited(int attempt, TimeSpan retryAfter, TimeSpan totalWaited, string reason)
            {
                int seconds = (int)Math.Ceiling(retryAfter.TotalSeconds);
                int totalSeconds = (int)Math.Ceiling(totalWaited.TotalSeconds);
                _provider.EnqueueWrite(() => _provider.WriteWarning(
                    $"Dropbox returned a transient error ({reason}). Waiting {seconds}s before retry. Press Ctrl+C to cancel."));
                _provider.EnqueueWrite(() => _provider.WriteVerbose(
                    $"Transient retry: attempt #{attempt} failed ({reason}); waiting {seconds}s; cumulative wait so far {totalSeconds}s."));
            }
        }

        #endregion

        #region Path Helpers

        private DropboxServiceClient GetService()
        {
            var drive = ResolveDriveInfo();
            if (drive != null)
                return drive.Service;

            throw new InvalidOperationException(
                "No Dropbox drive found. Use New-PSDrive or Connect-Dropbox first.");
        }

        private MetadataCache? GetCache()
        {
            return ResolveDriveInfo()?.Cache;
        }

        /// <summary>
        /// Resolves the active <see cref="DropboxDriveInfo"/> for the current
        /// operation. Prefers the operation's <see cref="CmdletProvider.PSDriveInfo"/>,
        /// but falls back to the provider's registered drives when it is null.
        /// PowerShell supplies a null <c>PSDriveInfo</c> when resolving a
        /// provider-qualified path (e.g. an item's <c>PSPath</c>,
        /// <c>DbxProvider\Dropbox::Foo</c>); without this fallback such paths
        /// resolve against no drive and silently return nothing. This mirrors how
        /// the built-in FileSystem/Registry providers resolve their backing store
        /// from the path/registered drives rather than relying on the drive
        /// context alone.
        /// </summary>
        private DropboxDriveInfo? ResolveDriveInfo()
        {
            if (PSDriveInfo is DropboxDriveInfo current)
                return current;

            var drives = ProviderInfo?.Drives;
            if (drives == null)
                return null;

            DropboxDriveInfo? firstDbx = null;
            int dbxCount = 0;
            foreach (var d in drives)
            {
                if (d is DropboxDriveInfo dbx)
                {
                    firstDbx ??= dbx;
                    dbxCount++;
                }
            }

            // With exactly one Dropbox drive (the common single-account case) the
            // mapping is unambiguous. With several drives a provider-qualified
            // path has lost the drive name, so the first registered Dropbox drive
            // is the best available match.
            if (dbxCount > 1)
                WriteVerbose("Resolving a drive-less Dropbox path against the first of "
                    + dbxCount + " registered Dropbox drives.");
            return firstDbx;
        }

        private static string ToDropboxPath(string providerPath)
        {
            return DropboxServiceClient.NormalizePath(providerPath);
        }

        /// <summary>
        /// Emits a <see cref="DropboxItem"/> through the provider with its
        /// <c>Path</c> property shadowed by a drive-qualified provider path
        /// (e.g. <c>Dbx:\Folder\file</c>) and the raw API path preserved on a
        /// <c>DropboxPath</c> note property. <see cref="DropboxItem"/> exposes a
        /// public <c>Path</c> property holding the raw API path (<c>/Folder/file</c>);
        /// when an item is piped to <c>Remove-Item</c>/<c>Get-Item</c>, the cmdlet's
        /// <c>-Path</c> binds that property ahead of <c>PSPath</c>, so a bare API
        /// path would be rooted against the current (possibly filesystem) location
        /// instead of routing back through this provider. Shadowing <c>Path</c> with
        /// the drive-qualified value makes <c>Get-ChildItem Dbx:\... | Remove-Item</c>
        /// delete the Dropbox item rather than a same-named local path.
        /// </summary>
        private void WriteDropboxItemObject(DropboxItem item, string providerPath, bool isContainer)
        {
            var pso = DropboxItemShaping.ToDriveQualifiedPSObject(item, ResolveDriveName());
            WriteItemObject(pso, providerPath, isContainer);
        }

        /// <summary>Resolves the active drive name (e.g. <c>Dbx</c>), falling back to
        /// the default when no drive is in scope.</summary>
        private string ResolveDriveName() =>
            (PSDriveInfo as DropboxDriveInfo)?.Name
            ?? ResolveDriveInfo()?.Name
            ?? "Dbx";

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

                return Run(ct => service.ItemExistsAsync(path, cancellationToken: ct));
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
                var item = Run(ct => service.GetMetadataAsync(path, cancellationToken: ct));
                WriteDropboxItemObject(item, item.Path, item.IsFolder);
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
                var item = Run(ct => service.GetMetadataAsync(path, cancellationToken: ct));
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
                if (ContainsRoutingWildcard(segments[i]))
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

        /// <summary>
        /// Reports whether a path segment carries a genuine globbing wildcard
        /// (<c>*</c> or <c>?</c>) that should route recursive enumeration to
        /// search_v2. Unlike <see cref="WildcardPattern.ContainsWildcardCharacters"/>,
        /// this deliberately ignores <c>[</c>/<c>]</c> so a literal bracketed folder
        /// name (e.g. <c>[archive]</c>) enumerates normally instead of being capped at
        /// 1000 search hits (issue #90). A backtick escapes the following character so
        /// an escaped <c>`*</c> is treated as a literal.
        /// </summary>
        private static bool ContainsRoutingWildcard(string segment)
        {
            for (int i = 0; i < segment.Length; i++)
            {
                char c = segment[i];
                if (c == '`') { i++; continue; }
                if (c == '*' || c == '?') return true;
            }
            return false;
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
                var cache = GetCache();
                var items = cache != null
                    ? cache.GetChildren(path)
                    : Run(ct => service.ListFolderAsync(path, cancellationToken: ct));
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
                var dynParams = GetNoSearchParams();
                var noSearch = dynParams?.NoSearch.IsPresent == true;
                var fileOnly = dynParams?.File.IsPresent == true;
                var dirOnly = dynParams?.Directory.IsPresent == true;
                if (fileOnly && dirOnly) { fileOnly = dirOnly = false; }

                bool ItemKindMatches(DropboxItem item) =>
                    (!fileOnly || !item.IsFolder) && (!dirOnly || item.IsFolder);

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
                        foreach (var item in found.Where(ItemKindMatches).OrderBy(i => !i.IsFolder).ThenBy(i => i.Name))
                        {
                            var providerPath = item.Path.Replace('/', '\\').TrimStart('\\');
                            WriteDropboxItemObject(item, providerPath, item.IsFolder);
                        }
                        return;
                    }

                    if (!recurse && pathHasWildcard && pathPattern != null
                        && pathPattern.Contains('*') == false && pathPattern.Contains('?') == false)
                    {
                        // No-op branch placeholder; PS handles single-folder wildcards.
                    }
                }

                var cache = GetCache();

                // Recursive enumeration walks the subtree one directory at a time so a
                // very large account is never buffered entirely in memory. Each folder's
                // children are fetched live (cursor-validated and cached) and sorted on
                // their own; peak memory is bounded by the largest single folder plus the
                // pending-folder stack, rather than the whole subtree, and items stream as
                // they are read instead of being collected and globally sorted first.
                if (recurse && cache != null && cache.Options.Enabled)
                {
                    WriteVerbose($"Get-ChildItem: streaming recursive enumeration from cache (path='{path}')");
                    foreach (var item in StreamRecursiveFromCache(cache, path))
                    {
                        if (!ItemKindMatches(item)) continue;
                        var providerPath = item.Path.Replace('/', '\\').TrimStart('\\');
                        WriteDropboxItemObject(item, providerPath, item.IsFolder);
                    }
                    return;
                }

                WriteVerbose($"Get-ChildItem: routing to list_folder (path='{path}', recurse={recurse})");
                var items = (cache != null && !recurse)
                    ? cache.GetChildren(path)
                    : Run(ct => service.ListFolderAsync(path, recursive: recurse, cancellationToken: ct));
                foreach (var item in items.Where(ItemKindMatches).OrderBy(i => !i.IsFolder).ThenBy(i => i.Name))
                {
                    var providerPath = item.Path.Replace('/', '\\').TrimStart('\\');
                    WriteDropboxItemObject(item, providerPath, item.IsFolder);
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

        /// <summary>
        /// Streams a subtree from the metadata cache one directory at a time using an
        /// explicit stack (depth-first, pre-order). Each directory's children are
        /// fetched live by <see cref="MetadataCache.GetChildren(string, System.Threading.CancellationToken)"/>
        /// (cursor-validated and cached), sorted sub-folders-first then files, and
        /// yielded before descending into its sub-folders in order. Only one directory's
        /// children plus the pending-folder stack are held, so peak memory is bounded by
        /// the largest single folder rather than the whole subtree, and the walk honors
        /// Ctrl+C via <see cref="System.Management.Automation.Provider.CmdletProvider.Stopping"/>.
        /// The per-folder API cost on large cold subtrees is tracked for optimization in
        /// issue #93.
        /// </summary>
        private IEnumerable<DropboxItem> StreamRecursiveFromCache(MetadataCache cache, string startPath)
        {
            var pending = new Stack<string>();
            pending.Push(startPath);
            while (pending.Count > 0)
            {
                if (Stopping) yield break;

                var folder = pending.Pop();
                var children = Run(ct => cache.GetChildrenAsync(folder, ct));
                SortDirectory(children);
                foreach (var child in children)
                {
                    if (Stopping) yield break;
                    yield return child;
                }

                // Push sub-folders in reverse so they pop (and emit) in sorted order.
                for (int i = children.Count - 1; i >= 0; i--)
                {
                    if (children[i].IsFolder) pending.Push(children[i].Path);
                }
            }
        }

        /// <summary>Orders one directory's children in place: sub-folders first, then
        /// files, alphabetical by name within each group. Sorting the list
        /// <see cref="MetadataCache.GetChildren(string, System.Threading.CancellationToken)"/>
        /// already returns avoids the extra buffer a LINQ <c>OrderBy</c> would allocate,
        /// keeping per-directory peak memory minimal. The name comparison matches
        /// <c>OrderBy(...).ThenBy(i =&gt; i.Name)</c> (default string comparer).</summary>
        private static void SortDirectory(List<DropboxItem> directory) =>
            directory.Sort(static (a, b) =>
                a.IsFolder != b.IsFolder
                    ? (a.IsFolder ? -1 : 1)
                    : Comparer<string>.Default.Compare(a.Name, b.Name));

        protected override void GetChildNames(string path, ReturnContainers returnContainers)
        {
            try
            {
                var service = GetService();
                var cache = GetCache();
                var items = cache != null
                    ? cache.GetChildren(path)
                    : Run(ct => service.ListFolderAsync(path, cancellationToken: ct));
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
                        var item = Run(ct => service.CreateFolderAsync(path, cancellationToken: ct));
                        GetCache()?.ApplyLocalAdd(item);
                        WriteDropboxItemObject(item, item.Path.Replace('/', '\\').TrimStart('\\'), true);
                    }
                }
                else
                {
                    if (ShouldProcess(path, "Create file"))
                    {
                        var content = newItemValue?.ToString() ?? "";
                        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(content));
                        var item = Run(ct => service.UploadAsync(path, stream, cancellationToken: ct));
                        GetCache()?.ApplyLocalAdd(item);
                        WriteDropboxItemObject(item, item.Path.Replace('/', '\\').TrimStart('\\'), false);
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

                if (ShouldProcess(path, "Delete"))
                {
                    Run(ct => service.DeleteAsync(path, cancellationToken: ct));
                    GetCache()?.ApplyLocalRemove(path);
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
                    var item = Run(ct => service.CopyAsync(path, copyPath, cancellationToken: ct));
                    GetCache()?.ApplyLocalAdd(item);
                    WriteDropboxItemObject(item, item.Path.Replace('/', '\\').TrimStart('\\'), item.IsFolder);
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
                    var item = Run(ct => service.MoveAsync(path, destination, cancellationToken: ct));
                    var cache = GetCache();
                    cache?.ApplyLocalRemove(path);
                    cache?.ApplyLocalAdd(item);
                    WriteDropboxItemObject(item, item.Path.Replace('/', '\\').TrimStart('\\'), item.IsFolder);
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
                    var item = Run(ct => service.MoveAsync(path, newPath, cancellationToken: ct));
                    var cache = GetCache();
                    cache?.ApplyLocalRemove(path);
                    cache?.ApplyLocalAdd(item);
                    WriteDropboxItemObject(item, item.Path.Replace('/', '\\').TrimStart('\\'), item.IsFolder);
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
            // PowerShell invokes ClearContent as the implicit truncate that precedes
            // every Set-Content/Out-File/redirection BEFORE handing the content to
            // GetContentWriter. During that implicit clear the active cmdlet is the
            // content writer, so the provider's DynamicParameters are the writer's
            // (DropboxContentWriterDynamicParameters). The writer then performs a
            // WriteMode.Overwrite upload that already truncates and replaces the file,
            // so uploading a separate zero-byte revision here is redundant -- and
            // dangerous, because a concurrent Dropbox sync client can race that
            // zero-byte intermediate into a zero-byte "conflicted copy". Skip it and
            // let the writer's overwrite be the single revision.
            //
            // An explicit Clear-Content has no following writer; ClearContentDynamicParameters
            // returns null, so DynamicParameters is not the writer's type and we must
            // upload zero bytes to truncate the file on the server.
            if (DynamicParameters is DropboxContentWriterDynamicParameters)
            {
                return;
            }

            try
            {
                var service = GetService();
                if (ShouldProcess(path, "Clear content"))
                {
                    using var empty = new MemoryStream();
                    Run(ct => service.UploadAsync(path, empty, cancellationToken: ct));
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
                var item = Run(ct => service.GetMetadataAsync(path, cancellationToken: ct));
                var pso = new PSObject();

                var properties = new (string Name, object? Value)[]
                {
                    ("Name", item.Name),
                    ("Path", item.Path),
                    ("Id", item.Id),
                    ("IsFolder", item.IsFolder),
                    ("Length", item.Length),
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
    /// Dynamic parameters exposed by Get-ChildItem (and Test-Path) on the
    /// Dropbox provider.
    ///   -NoSearch  : force the list-based path (skip search_v2). Useful
    ///                right after uploads while the search index lags.
    ///   -File      : return only files (FileSystem-parity post-filter).
    ///   -Directory : return only folders (FileSystem-parity post-filter).
    /// </summary>
    public class NoSearchDynamicParameters
    {
        [Parameter]
        public SwitchParameter NoSearch { get; set; }

        [Parameter]
        public SwitchParameter File { get; set; }

        [Parameter]
        public SwitchParameter Directory { get; set; }
    }
}