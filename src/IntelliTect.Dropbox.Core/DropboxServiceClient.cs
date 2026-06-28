using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dropbox.Api;
using Dropbox.Api.Files;
using Dropbox.Api.Sharing;
using Dropbox.Api.Users;

namespace IntelliTect.Dropbox
{
    /// <summary>Comprehensive wrapper around the Dropbox API v2.</summary>
    public class DropboxServiceClient : IDisposable
    {
        private readonly DropboxClient _client;
        private const int UploadSessionChunkSize = 8 * 1024 * 1024;
        private const long UploadSessionThreshold = 150L * 1024 * 1024;

        // Test-only overrides for chunked-upload sizing. Production callers
        // never set these. Functional tests can dial them down (e.g. 4 MB
        // threshold / 1 MB chunks) so the chunked-upload code path is
        // exercised end-to-end without uploading > 150 MB on every CI run.
        internal static long? UploadSessionThresholdOverride { get; set; }
        internal static int? UploadSessionChunkSizeOverride { get; set; }

        private static long EffectiveThreshold => UploadSessionThresholdOverride ?? UploadSessionThreshold;
        private static int EffectiveChunkSize => UploadSessionChunkSizeOverride ?? UploadSessionChunkSize;

        public DropboxServiceClient(string accessToken)
        {
            _client = new DropboxClient(accessToken);
        }

        public DropboxServiceClient(string refreshToken, string appKey, string appSecret)
        {
            _client = new DropboxClient(refreshToken, appKey, appSecret);
        }

        public DropboxServiceClient(DropboxClient client)
        {
            _client = client;
        }


        // --- Rate limiting / cancellation infrastructure ---
        private IRateLimitNotifier? _rateLimitNotifier;
        private IDelay _rateLimitDelay = SystemDelay.Instance;
        private IRateLimitSimulator? _rateLimitSimulator = CompositeRateLimitSimulator.Default;

        /// <summary>Inject a notifier so cmdlets can surface
        /// <c>WriteWarning</c>/<c>WriteVerbose</c> during rate-limit waits.</summary>
        public void SetRateLimitNotifier(IRateLimitNotifier? notifier) => _rateLimitNotifier = notifier;

        /// <summary>For tests: inject an <see cref="IDelay"/> so the retry loop
        /// doesn't actually sleep.</summary>
        internal void SetDelay(IDelay delay) => _rateLimitDelay = delay ?? SystemDelay.Instance;

        /// <summary>For tests / advanced scenarios: replace the rate-limit
        /// simulator. Pass <c>null</c> to disable simulation entirely.</summary>
        internal void SetRateLimitSimulator(IRateLimitSimulator? simulator) => _rateLimitSimulator = simulator;

        private Task<T> RetryAsync<T>(Func<CancellationToken, Task<T>> op, CancellationToken cancellationToken) =>
            RateLimitRetry.ExecuteAsync(op, _rateLimitNotifier, _rateLimitDelay, _rateLimitSimulator, cancellationToken);

        private Task RetryAsync(Func<CancellationToken, Task> op, CancellationToken cancellationToken) =>
            RateLimitRetry.ExecuteAsync(op, _rateLimitNotifier, _rateLimitDelay, _rateLimitSimulator, cancellationToken);

        #region Path Helpers

        public static string NormalizePath(string path)
        {
            if (string.IsNullOrEmpty(path) || path == "\\" || path == "/" || path == ".")
                return "";
            path = path.Replace('\\', '/');
            // Strip a leading PowerShell drive qualifier (e.g. "Dbx:/Folder/file"
            // produced when a provider path such as "Dbx:\Folder\file" is piped into
            // a Dropbox API cmdlet). Dropbox file/folder names cannot contain ':', so
            // a colon appearing before the first '/' can only be a drive qualifier;
            // without this the path becomes "/Dbx:/Folder/file" and the API rejects it.
            int colon = path.IndexOf(':');
            if (colon >= 0)
            {
                int slash = path.IndexOf('/');
                if (slash < 0 || colon < slash)
                    path = path.Substring(colon + 1);
            }
            if (string.IsNullOrEmpty(path) || path == "/")
                return "";
            if (!path.StartsWith("/"))
                path = "/" + path;
            return path.TrimEnd('/');
        }

        #endregion

        #region Files - List / Get Metadata

        public virtual Task<List<DropboxItem>> ListFolderAsync(string path, bool recursive = false, bool includeDeleted = false, CancellationToken cancellationToken = default) =>
            RetryAsync(_ => ListFolderCoreAsync(path, recursive, includeDeleted), cancellationToken);

        private async Task<List<DropboxItem>> ListFolderCoreAsync(string path, bool recursive = false, bool includeDeleted = false)
        {
            var (items, _) = await ListFolderWithCursorAsync(path, recursive, includeDeleted);
            return items;
        }

        /// <summary>
        /// Same as <see cref="ListFolderAsync"/> but also returns the final
        /// cursor representing the snapshot. Used by the metadata cache to
        /// validate freshness on subsequent reads via
        /// <see cref="ListFolderContinueRawAsync"/>.
        /// </summary>
        public virtual Task<(List<DropboxItem> Items, string Cursor)> ListFolderWithCursorAsync(string path, bool recursive = false, bool includeDeleted = false, CancellationToken cancellationToken = default) =>
            RetryAsync(_ => ListFolderWithCursorCoreAsync(path, recursive, includeDeleted), cancellationToken);

        private async Task<(List<DropboxItem> Items, string Cursor)> ListFolderWithCursorCoreAsync(string path, bool recursive = false, bool includeDeleted = false)
        {
            var dbxPath = NormalizePath(path);
            var items = new List<DropboxItem>();
            var result = await _client.Files.ListFolderAsync(dbxPath, recursive, includeDeleted: includeDeleted,
                includeHasExplicitSharedMembers: true, includeMountedFolders: true);
            items.AddRange(result.Entries.Select(MapMetadataToItem));
            var cursor = result.Cursor;
            while (result.HasMore)
            {
                result = await _client.Files.ListFolderContinueAsync(result.Cursor);
                items.AddRange(result.Entries.Select(MapMetadataToItem));
                cursor = result.Cursor;
            }
            return (items, cursor);
        }

        /// <summary>One page of a <c>list_folder</c> enumeration.</summary>
        public sealed class ListFolderPage
        {
            /// <summary>Items returned by this single page.</summary>
            public List<DropboxItem> Items { get; } = new();

            /// <summary>Cursor describing the snapshot so far; pass to
            /// <see cref="ListFolderContinueRawAsync"/> for the next page.</summary>
            public string Cursor { get; set; } = string.Empty;

            /// <summary>True when more pages remain.</summary>
            public bool HasMore { get; set; }
        }

        /// <summary>
        /// Issues a single (first) <c>list_folder</c> call and returns just that
        /// page together with its cursor and a <see cref="ListFolderPage.HasMore"/>
        /// flag. Unlike <see cref="ListFolderWithCursorAsync"/> this does not drain
        /// every page, so callers can walk a large recursive listing one page at a
        /// time and persist progress between pages. Optionally requests media info
        /// and explicit-shared-member flags, which a recursive listing returns at
        /// no extra round-trip cost.
        /// </summary>
        public virtual Task<ListFolderPage> ListFolderFirstPageAsync(string path, bool recursive = false,
            bool includeDeleted = false, bool includeMediaInfo = false,
            bool includeHasExplicitSharedMembers = false, CancellationToken cancellationToken = default) =>
            RetryAsync(_ => ListFolderFirstPageCoreAsync(path, recursive, includeDeleted, includeMediaInfo,
                includeHasExplicitSharedMembers), cancellationToken);

        private async Task<ListFolderPage> ListFolderFirstPageCoreAsync(string path, bool recursive,
            bool includeDeleted, bool includeMediaInfo, bool includeHasExplicitSharedMembers)
        {
            var dbxPath = NormalizePath(path);
            var result = await _client.Files.ListFolderAsync(dbxPath, recursive,
                includeMediaInfo: includeMediaInfo, includeDeleted: includeDeleted,
                includeHasExplicitSharedMembers: includeHasExplicitSharedMembers, includeMountedFolders: true);
            var page = new ListFolderPage { Cursor = result.Cursor, HasMore = result.HasMore };
            page.Items.AddRange(result.Entries.Select(MapMetadataToItem));
            return page;
        }

        /// <summary>
        /// Result of a delta-fetch via /files/list_folder/continue.
        /// </summary>
        public sealed class ListFolderDelta
        {
            public List<DropboxItem> AddsOrUpdates { get; } = new();
            /// <summary>Lowercased paths of removed entries (folders or files; type unknown).</summary>
            public List<string> Removes { get; } = new();
            public string NewCursor { get; set; } = string.Empty;
            public bool HasMore { get; set; }
            /// <summary>True when the cursor was rejected; caller must full-refresh.</summary>
            public bool ResetRequired { get; set; }
        }

        /// <summary>
        /// Calls /files/list_folder/continue once. Returns a delta of
        /// adds+removes since the cursor. Detects cursor invalidation and
        /// signals it via <see cref="ListFolderDelta.ResetRequired"/>.
        /// </summary>
        public virtual Task<ListFolderDelta> ListFolderContinueRawAsync(string cursor, CancellationToken cancellationToken = default) =>
            RetryAsync(_ => ListFolderContinueRawCoreAsync(cursor), cancellationToken);

        private async Task<ListFolderDelta> ListFolderContinueRawCoreAsync(string cursor)
        {
            var delta = new ListFolderDelta();
            try
            {
                var result = await _client.Files.ListFolderContinueAsync(cursor);
                foreach (var entry in result.Entries)
                {
                    if (entry.IsDeleted)
                    {
                        var p = entry.PathLower ?? entry.PathDisplay ?? entry.Name;
                        delta.Removes.Add(p.ToLowerInvariant());
                    }
                    else
                    {
                        delta.AddsOrUpdates.Add(MapMetadataToItem(entry));
                    }
                }
                delta.NewCursor = result.Cursor;
                delta.HasMore = result.HasMore;
            }
            catch (ApiException<ListFolderContinueError> ex) when (ex.ErrorResponse.IsReset)
            {
                delta.ResetRequired = true;
            }
            catch (BadInputException)
            {
                // Cursor was rejected as malformed/expired by the server. Treat
                // as a reset so the cache can fall back to a full enumeration.
                delta.ResetRequired = true;
            }
            return delta;
        }

        /// <summary>
        /// Returns a cursor describing the current state of <paramref name="path"/>
        /// via <c>/files/list_folder/get_latest_cursor</c>, WITHOUT enumerating any
        /// entries. A later <see cref="ListFolderContinueRawAsync"/> from this cursor
        /// yields everything that changed since this call. Because the call never
        /// lists entries it returns in constant time and cannot wedge on a huge
        /// subtree, making it the safe way to capture an account-wide sync anchor
        /// before a long build.
        /// </summary>
        public virtual Task<string> GetLatestCursorAsync(string path, bool recursive = false, CancellationToken cancellationToken = default) =>
            RetryAsync(_ => GetLatestCursorCoreAsync(path, recursive), cancellationToken);

        private async Task<string> GetLatestCursorCoreAsync(string path, bool recursive)
        {
            var dbxPath = NormalizePath(path);
            var result = await _client.Files.ListFolderGetLatestCursorAsync(dbxPath, recursive,
                includeHasExplicitSharedMembers: true, includeMountedFolders: true);
            return result.Cursor;
        }

        public virtual Task<DropboxItem> GetMetadataAsync(string path, bool includeDeleted = false, CancellationToken cancellationToken = default) =>
            RetryAsync(_ => GetMetadataCoreAsync(path, includeDeleted), cancellationToken);

        private async Task<DropboxItem> GetMetadataCoreAsync(string path, bool includeDeleted = false)
        {
            var dbxPath = NormalizePath(path);
            if (string.IsNullOrEmpty(dbxPath))
                return new DropboxItem { Name = "", Path = "/", IsFolder = true, Id = "root" };
            var metadata = await _client.Files.GetMetadataAsync(dbxPath, includeDeleted: includeDeleted,
                includeHasExplicitSharedMembers: true);
            return MapMetadataToItem(metadata);
        }

        public virtual Task<bool> ItemExistsAsync(string path, CancellationToken cancellationToken = default) =>
            RetryAsync(_ => ItemExistsCoreAsync(path), cancellationToken);

        private async Task<bool> ItemExistsCoreAsync(string path)
        {
            try { await GetMetadataAsync(path); return true; }
            catch (ApiException<GetMetadataError>) { return false; }
        }

        #endregion

        #region Files - Download / Upload

        public virtual Task<(Stream Content, DropboxItem Metadata)> DownloadAsync(string path, CancellationToken cancellationToken = default) =>
            RetryAsync(_ => DownloadCoreAsync(path), cancellationToken);

        private async Task<(Stream Content, DropboxItem Metadata)> DownloadCoreAsync(string path)
        {
            var response = await _client.Files.DownloadAsync(NormalizePath(path));
            return (await response.GetContentAsStreamAsync(), MapMetadataToItem(response.Response));
        }

        public Task<byte[]> DownloadBytesAsync(string path, CancellationToken cancellationToken = default) =>
            RetryAsync(_ => DownloadBytesCoreAsync(path), cancellationToken);

        private async Task<byte[]> DownloadBytesCoreAsync(string path)
        {
            var response = await _client.Files.DownloadAsync(NormalizePath(path));
            return await response.GetContentAsByteArrayAsync();
        }

        public virtual Task<DropboxItem> UploadAsync(string path, Stream content, WriteMode? mode = null, CancellationToken cancellationToken = default) =>
            RetryAsync(_ => UploadCoreAsync(path, content, mode), cancellationToken);

        private async Task<DropboxItem> UploadCoreAsync(string path, Stream content, WriteMode? mode = null)
        {
            var dbxPath = NormalizePath(path);
            mode ??= WriteMode.Overwrite.Instance;
            if (content.CanSeek && content.Length <= EffectiveThreshold)
            {
                var metadata = await _client.Files.UploadAsync(dbxPath, mode: mode, body: content);
                return MapMetadataToItem(metadata);
            }
            return await UploadSessionAsync(dbxPath, content, mode);
        }

        private async Task<DropboxItem> UploadSessionAsync(string path, Stream content, WriteMode mode)
        {
            int chunkSize = EffectiveChunkSize;
            var buffer = new byte[chunkSize];
            int bytesRead = await content.ReadAsync(buffer, 0, chunkSize);

            using var firstChunk = new MemoryStream(buffer, 0, bytesRead);
            var session = await _client.Files.UploadSessionStartAsync(body: firstChunk);
            ulong offset = (ulong)bytesRead;

            while (true)
            {
                bytesRead = await content.ReadAsync(buffer, 0, chunkSize);
                if (bytesRead <= 0) break;

                bool isLast = (content.CanSeek && content.Position >= content.Length) || bytesRead < chunkSize;

                if (isLast)
                {
                    using var lastChunk = new MemoryStream(buffer, 0, bytesRead);
                    var cursor = new UploadSessionCursor(session.SessionId, offset);
                    var commit = new CommitInfo(path, mode);
                    var result = await _client.Files.UploadSessionFinishAsync(cursor, commit, body: lastChunk);
                    return MapMetadataToItem(result);
                }

                using var chunk = new MemoryStream(buffer, 0, bytesRead);
                await _client.Files.UploadSessionAppendV2Async(
                    new UploadSessionCursor(session.SessionId, offset), body: chunk);
                offset += (ulong)bytesRead;
            }

            // Finish with empty body if we ran out of data
            using var empty = new MemoryStream();
            var finalCursor = new UploadSessionCursor(session.SessionId, offset);
            var finalResult = await _client.Files.UploadSessionFinishAsync(
                finalCursor, new CommitInfo(path, mode), body: empty);
            return MapMetadataToItem(finalResult);
        }

        #endregion

        #region Files - Copy / Move / Delete / Create Folder

        public Task<DropboxItem> CopyAsync(string fromPath, string toPath, CancellationToken cancellationToken = default) =>
            RetryAsync(_ => CopyCoreAsync(fromPath, toPath), cancellationToken);

        private async Task<DropboxItem> CopyCoreAsync(string fromPath, string toPath)
        {
            var result = await _client.Files.CopyV2Async(NormalizePath(fromPath), NormalizePath(toPath));
            return MapMetadataToItem(result.Metadata);
        }

        public Task<DropboxItem> MoveAsync(string fromPath, string toPath, CancellationToken cancellationToken = default) =>
            RetryAsync(_ => MoveCoreAsync(fromPath, toPath), cancellationToken);

        private async Task<DropboxItem> MoveCoreAsync(string fromPath, string toPath)
        {
            var result = await _client.Files.MoveV2Async(NormalizePath(fromPath), NormalizePath(toPath));
            return MapMetadataToItem(result.Metadata);
        }

        public virtual Task DeleteAsync(string path, CancellationToken cancellationToken = default) =>
            RetryAsync(_ => DeleteCoreAsync(path), cancellationToken);

        private async Task DeleteCoreAsync(string path)
        {
            var dbxPath = NormalizePath(path);
            await _client.Files.DeleteV2Async(dbxPath);
        }

        public Task<DropboxItem> CreateFolderAsync(string path, CancellationToken cancellationToken = default) =>
            RetryAsync(_ => CreateFolderCoreAsync(path), cancellationToken);

        private async Task<DropboxItem> CreateFolderCoreAsync(string path)
        {
            var result = await _client.Files.CreateFolderV2Async(NormalizePath(path));
            return MapMetadataToItem(result.Metadata);
        }

        #endregion

        #region Files - Search

        public Task<List<DropboxSearchResult>> SearchAsync(string query, string path = "",
            int maxResults = 100, bool includeHighlights = false,
            bool filenameOnly = false,
            IEnumerable<string>? fileExtensions = null,
            IEnumerable<FileCategory>? fileCategories = null,
            FileStatus? fileStatus = null,
            SearchOrderBy? orderBy = null, CancellationToken cancellationToken = default) =>
            RetryAsync(_ => SearchCoreAsync(query, path, maxResults, includeHighlights, filenameOnly, fileExtensions, fileCategories, fileStatus, orderBy), cancellationToken);

        private async Task<List<DropboxSearchResult>> SearchCoreAsync(string query, string path = "",
            int maxResults = 100, bool includeHighlights = false,
            bool filenameOnly = false,
            IEnumerable<string>? fileExtensions = null,
            IEnumerable<FileCategory>? fileCategories = null,
            FileStatus? fileStatus = null,
            SearchOrderBy? orderBy = null)
        {
            var options = new SearchOptions(
                path: string.IsNullOrEmpty(path) ? null : NormalizePath(path),
                maxResults: (ulong)maxResults,
                orderBy: orderBy,
                fileStatus: fileStatus,
                filenameOnly: filenameOnly,
                fileExtensions: fileExtensions,
                fileCategories: fileCategories);

            SearchMatchFieldOptions? matchFieldOptions = includeHighlights
                ? new SearchMatchFieldOptions(includeHighlights: true) : null;

            var result = await _client.Files.SearchV2Async(query, options, matchFieldOptions);
            var results = new List<DropboxSearchResult>();

            void ProcessMatches(IEnumerable<SearchMatchV2> matches)
            {
                foreach (var match in matches)
                {
                    if (match.Metadata?.AsMetadata?.Value is Metadata md)
                    {
                        string matchType;
                        try
                        {
                            matchType = match.MatchType?.IsFilename == true ? "Filename" :
                                        match.MatchType?.IsFileContent == true ? "Content" :
                                        match.MatchType?.IsFilenameAndContent == true ? "FilenameAndContent" :
                                        match.MatchType?.IsImageContent == true ? "ImageContent" :
                                        "Unknown";
                        }
                        catch { matchType = "Unknown"; }

                        results.Add(new DropboxSearchResult { MatchType = matchType, Item = MapMetadataToItem(md) });
                    }
                }
            }

            ProcessMatches(result.Matches);
            while (result.HasMore && results.Count < maxResults)
            {
                result = await _client.Files.SearchContinueV2Async(result.Cursor);
                ProcessMatches(result.Matches);
            }
            return results;
        }

        /// <summary>
        /// Filename-only search using a PowerShell wildcard pattern. Dropbox's
        /// search_v2 is prefix-token-based (not glob), so we derive a token
        /// query from the pattern, then post-filter the results with
        /// <see cref="WildcardMatcher"/> to enforce true PowerShell wildcard
        /// semantics without depending on System.Management.Automation.
        /// </summary>
        public Task<List<DropboxItem>> SearchByFilenameAsync(string pattern,
            string path = "", int maxResults = 1000, CancellationToken cancellationToken = default) =>
            RetryAsync(_ => SearchByFilenameCoreAsync(pattern, path, maxResults), cancellationToken);

        private async Task<List<DropboxItem>> SearchByFilenameCoreAsync(string pattern,
            string path = "", int maxResults = 1000)
        {
            var wildcard = new WildcardMatcher(pattern);

            // Convert PS wildcard pattern to a Dropbox token query: split on
            // wildcard chars and path/filename separators, keep tokens of >=2
            // chars (Dropbox prefix-matches tokens). If nothing usable remains,
            // fall back to listing the path.
            var tokens = pattern
                .Split(new[] { '*', '?', '[', ']', '/', '\\', ' ', '.', '_', '-' },
                    StringSplitOptions.RemoveEmptyEntries)
                .Where(t => t.Length >= 2)
                .ToArray();

            // Try to harvest a useful extension filter from a trailing ".ext"
            // segment of the pattern (only when the extension itself contains
            // no wildcards).
            string? extension = null;
            var lastDot = pattern.LastIndexOf('.');
            if (lastDot >= 0 && lastDot < pattern.Length - 1)
            {
                var tail = pattern.Substring(lastDot + 1);
                if (tail.IndexOfAny(new[] { '*', '?', '[', ']', '/', '\\' }) < 0
                    && tail.Length >= 1)
                {
                    extension = tail;
                }
            }

            var query = tokens.Length > 0 ? string.Join(" ", tokens) : "";

            // If we can't form any query and have no extension, the pattern is
            // pure wildcards (e.g. "*"); fall back to a recursive listing
            // filtered client-side.
            if (string.IsNullOrEmpty(query) && extension == null)
            {
                var listed = await ListFolderAsync(path, recursive: true);
                return listed.Where(i => wildcard.IsMatch(i.Name)).Take(maxResults).ToList();
            }

            // Extension-only patterns (e.g. "*.txt", "*.pdf") have no useful
            // search token — the only "token" we'd derive equals the extension,
            // and Dropbox's search_v2 is unreliable for such short, common
            // tokens (often returning zero hits even when matching files
            // exist). Fall back to a recursive listing filtered client-side,
            // which is authoritative for filename matching.
            if (extension != null
                && (tokens.Length == 0
                    || (tokens.Length == 1
                        && string.Equals(tokens[0], extension, StringComparison.OrdinalIgnoreCase))))
            {
                var listed = await ListFolderAsync(path, recursive: true);
                return listed.Where(i => wildcard.IsMatch(i.Name)).Take(maxResults).ToList();
            }

            var raw = await SearchAsync(
                query: query,
                path: path,
                maxResults: maxResults,
                includeHighlights: false,
                filenameOnly: true,
                fileExtensions: extension != null ? new[] { extension } : null,
                orderBy: SearchOrderBy.Relevance.Instance);

            return raw
                .Where(r => r.Item != null && wildcard.IsMatch(r.Item.Name))
                .Select(r => r.Item!)
                .ToList();
        }

        #endregion

        #region Files - Revisions

        public virtual Task<List<DropboxRevision>> ListRevisionsAsync(string path, int limit = 10, CancellationToken cancellationToken = default) =>
            RetryAsync(_ => ListRevisionsCoreAsync(path, limit), cancellationToken);

        private async Task<List<DropboxRevision>> ListRevisionsCoreAsync(string path, int limit = 10)
        {
            var result = await _client.Files.ListRevisionsAsync(NormalizePath(path), limit: (ulong)limit);
            return result.Entries.Select(e => new DropboxRevision
            {
                Name = e.Name,
                Path = e.PathDisplay ?? e.PathLower ?? "",
                Rev = e.Rev,
                Length = e.Size,
                ServerModified = e.ServerModified,
                ClientModified = e.ClientModified,
                ContentHash = e.ContentHash ?? "",
                IsDeleted = result.IsDeleted
            }).ToList();
        }

        public Task<DropboxItem> RestoreAsync(string path, string rev, CancellationToken cancellationToken = default) =>
            RetryAsync(_ => RestoreCoreAsync(path, rev), cancellationToken);

        private async Task<DropboxItem> RestoreCoreAsync(string path, string rev)
        {
            var metadata = await _client.Files.RestoreAsync(NormalizePath(path), rev);
            return MapMetadataToItem(metadata);
        }

        #endregion

        #region Files - Temporary Link / Save URL

        public Task<string> GetTemporaryLinkAsync(string path, CancellationToken cancellationToken = default) =>
            RetryAsync(_ => GetTemporaryLinkCoreAsync(path), cancellationToken);

        private async Task<string> GetTemporaryLinkCoreAsync(string path)
        {
            var result = await _client.Files.GetTemporaryLinkAsync(NormalizePath(path));
            return result.Link;
        }

        public Task<string> SaveUrlAsync(string path, string url, CancellationToken cancellationToken = default) =>
            RetryAsync(_ => SaveUrlCoreAsync(path, url), cancellationToken);

        private async Task<string> SaveUrlCoreAsync(string path, string url)
        {
            var result = await _client.Files.SaveUrlAsync(NormalizePath(path), url);
            return result.IsAsyncJobId ? result.AsAsyncJobId.Value : "complete";
        }

        #endregion

        #region Files - Preview / Thumbnail / Export

        public Task<(byte[] Content, string ContentType)> GetPreviewAsync(string path, CancellationToken cancellationToken = default) =>
            RetryAsync(_ => GetPreviewCoreAsync(path), cancellationToken);

        private async Task<(byte[] Content, string ContentType)> GetPreviewCoreAsync(string path)
        {
            var result = await _client.Files.GetPreviewAsync(NormalizePath(path));
            return (await result.GetContentAsByteArrayAsync(), "application/pdf");
        }

        public Task<byte[]> GetThumbnailAsync(string path, string size = "w64h64", string format = "jpeg", CancellationToken cancellationToken = default) =>
            RetryAsync(_ => GetThumbnailCoreAsync(path, size, format), cancellationToken);

        private async Task<byte[]> GetThumbnailCoreAsync(string path, string size = "w64h64", string format = "jpeg")
        {
            ThumbnailSize thumbSize = size switch
            {
                "w32h32" => ThumbnailSize.W32h32.Instance,
                "w128h128" => ThumbnailSize.W128h128.Instance,
                "w256h256" => ThumbnailSize.W256h256.Instance,
                "w480h320" => ThumbnailSize.W480h320.Instance,
                "w640h480" => ThumbnailSize.W640h480.Instance,
                "w960h640" => ThumbnailSize.W960h640.Instance,
                "w1024h768" => ThumbnailSize.W1024h768.Instance,
                "w2048h1536" => ThumbnailSize.W2048h1536.Instance,
                _ => ThumbnailSize.W64h64.Instance
            };
            ThumbnailFormat thumbFormat = format.ToLowerInvariant() == "png"
                ? ThumbnailFormat.Png.Instance : ThumbnailFormat.Jpeg.Instance;

            var result = await _client.Files.GetThumbnailV2Async(
                new PathOrLink.Path(NormalizePath(path)), thumbFormat, thumbSize);
            return await result.GetContentAsByteArrayAsync();
        }

        public Task<(byte[] Content, DropboxItem Metadata)> ExportFileAsync(string path, CancellationToken cancellationToken = default) =>
            RetryAsync(_ => ExportFileCoreAsync(path), cancellationToken);

        private async Task<(byte[] Content, DropboxItem Metadata)> ExportFileCoreAsync(string path)
        {
            var result = await _client.Files.ExportAsync(NormalizePath(path));
            var bytes = await result.GetContentAsByteArrayAsync();
            return (bytes, new DropboxItem
            {
                Name = result.Response.FileMetadata?.Name ?? "",
                Length = result.Response.FileMetadata?.Size ?? 0,
                Path = result.Response.FileMetadata?.PathDisplay ?? ""
            });
        }

        #endregion

        #region Files - Tags

        public Task<List<DropboxTag>> GetTagsAsync(string path, CancellationToken cancellationToken = default) =>
            GetTagsAsync(new[] { path }, cancellationToken);

        public Task<List<DropboxTag>> GetTagsAsync(string[] paths, CancellationToken cancellationToken = default) =>
            RetryAsync(_ => GetTagsCoreAsync(paths), cancellationToken);

        private async Task<List<DropboxTag>> GetTagsCoreAsync(params string[] paths)
        {
            var pathList = paths.Select(p => NormalizePath(p)).ToList();
            var result = await _client.Files.TagsGetAsync(new GetTagsArg(pathList));
            var tags = new List<DropboxTag>();
            foreach (var pt in result.PathsToTags)
            {
                foreach (var tag in pt.Tags)
                {
                    var userTag = tag.AsUserGeneratedTag;
                    if (userTag != null)
                        tags.Add(new DropboxTag { Path = pt.Path, TagText = userTag.Value.TagText });
                }
            }
            return tags;
        }

        public Task AddTagAsync(string path, string tagText, CancellationToken cancellationToken = default) =>
            RetryAsync(_ => AddTagCoreAsync(path, tagText), cancellationToken);

        private async Task AddTagCoreAsync(string path, string tagText)
        {
            await _client.Files.TagsAddAsync(NormalizePath(path), tagText);
        }

        public Task RemoveTagAsync(string path, string tagText, CancellationToken cancellationToken = default) =>
            RetryAsync(_ => RemoveTagCoreAsync(path, tagText), cancellationToken);

        private async Task RemoveTagCoreAsync(string path, string tagText)
        {
            await _client.Files.TagsRemoveAsync(NormalizePath(path), tagText);
        }

        #endregion

        #region Sharing - Shared Links

        public Task<DropboxSharedLink> CreateSharedLinkAsync(string path,
            string? requestedVisibility = null, DateTime? expires = null, CancellationToken cancellationToken = default) =>
            RetryAsync(_ => CreateSharedLinkCoreAsync(path, requestedVisibility, expires), cancellationToken);

        private async Task<DropboxSharedLink> CreateSharedLinkCoreAsync(string path,
            string? requestedVisibility = null, DateTime? expires = null)
        {
            RequestedVisibility? vis = requestedVisibility switch
            {
                "public" => RequestedVisibility.Public.Instance,
                "team_only" => RequestedVisibility.TeamOnly.Instance,
                "password" => RequestedVisibility.Password.Instance,
                _ => null
            };
            var settings = new SharedLinkSettings(requestedVisibility: vis, expires: expires);
            var result = await _client.Sharing.CreateSharedLinkWithSettingsAsync(NormalizePath(path), settings);
            return MapSharedLink(result);
        }

        public Task<List<DropboxSharedLink>> ListSharedLinksAsync(string? path = null, string? cursor = null, CancellationToken cancellationToken = default) =>
            RetryAsync(_ => ListSharedLinksCoreAsync(path, cursor), cancellationToken);

        private async Task<List<DropboxSharedLink>> ListSharedLinksCoreAsync(string? path = null, string? cursor = null)
        {
            var dbxPath = path != null ? NormalizePath(path) : null;
            var result = await _client.Sharing.ListSharedLinksAsync(dbxPath, cursor);
            var links = result.Links.Select(MapSharedLink).ToList();
            while (result.HasMore)
            {
                result = await _client.Sharing.ListSharedLinksAsync(cursor: result.Cursor);
                links.AddRange(result.Links.Select(MapSharedLink));
            }
            return links;
        }

        public Task RevokeSharedLinkAsync(string url, CancellationToken cancellationToken = default) =>
            RetryAsync(_ => _client.Sharing.RevokeSharedLinkAsync(url), cancellationToken);

        public Task<DropboxSharedLink> GetSharedLinkMetadataAsync(string url, CancellationToken cancellationToken = default) =>
            RetryAsync(_ => GetSharedLinkMetadataCoreAsync(url), cancellationToken);

        private async Task<DropboxSharedLink> GetSharedLinkMetadataCoreAsync(string url)
        {
            var result = await _client.Sharing.GetSharedLinkMetadataAsync(url);
            return MapSharedLink(result);
        }

        #endregion

        #region Sharing - Folders

        public Task<string> ShareFolderAsync(string path, CancellationToken cancellationToken = default) =>
            RetryAsync(_ => ShareFolderCoreAsync(path), cancellationToken);

        private async Task<string> ShareFolderCoreAsync(string path)
        {
            var result = await _client.Sharing.ShareFolderAsync(NormalizePath(path));
            return result.IsComplete ? result.AsComplete.Value.SharedFolderId
                : result.AsAsyncJobId?.Value ?? "pending";
        }

        public Task UnshareFolderAsync(string sharedFolderId, bool leaveACopy = false, CancellationToken cancellationToken = default) =>
            RetryAsync(_ => _client.Sharing.UnshareFolderAsync(sharedFolderId, leaveACopy), cancellationToken);

        public Task<List<DropboxSharedFolder>> ListSharedFoldersAsync(CancellationToken cancellationToken = default) =>
            RetryAsync(_ => ListSharedFoldersCoreAsync(), cancellationToken);

        private async Task<List<DropboxSharedFolder>> ListSharedFoldersCoreAsync()
        {
            var result = await _client.Sharing.ListFoldersAsync();
            var folders = new List<DropboxSharedFolder>(result.Entries.Select(MapSharedFolder));
            while (!string.IsNullOrEmpty(result.Cursor))
            {
                result = await _client.Sharing.ListFoldersContinueAsync(result.Cursor);
                folders.AddRange(result.Entries.Select(MapSharedFolder));
            }
            return folders;
        }

        public Task<DropboxSharedFolder> GetSharedFolderMetadataAsync(string sharedFolderId, CancellationToken cancellationToken = default) =>
            RetryAsync(_ => GetSharedFolderMetadataCoreAsync(sharedFolderId), cancellationToken);

        private async Task<DropboxSharedFolder> GetSharedFolderMetadataCoreAsync(string sharedFolderId)
        {
            var result = await _client.Sharing.GetFolderMetadataAsync(sharedFolderId);
            return MapSharedFolder(result);
        }

        #endregion

        #region Sharing - Members

        public Task AddFolderMemberAsync(string sharedFolderId, string email, string accessLevel = "viewer", CancellationToken cancellationToken = default) =>
            RetryAsync(_ => AddFolderMemberCoreAsync(sharedFolderId, email, accessLevel), cancellationToken);

        private async Task AddFolderMemberCoreAsync(string sharedFolderId, string email, string accessLevel = "viewer")
        {
            var level = ParseAccessLevel(accessLevel);
            var member = new AddMember(new MemberSelector.Email(email), level);
            await _client.Sharing.AddFolderMemberAsync(sharedFolderId, new[] { member });
        }

        public Task RemoveFolderMemberAsync(string sharedFolderId, string email, CancellationToken cancellationToken = default) =>
            RetryAsync(_ => _client.Sharing.RemoveFolderMemberAsync(sharedFolderId, new MemberSelector.Email(email), false), cancellationToken);

        public Task<List<DropboxMember>> ListFolderMembersAsync(string sharedFolderId, CancellationToken cancellationToken = default) =>
            RetryAsync(_ => ListFolderMembersCoreAsync(sharedFolderId), cancellationToken);

        private async Task<List<DropboxMember>> ListFolderMembersCoreAsync(string sharedFolderId)
        {
            var result = await _client.Sharing.ListFolderMembersAsync(sharedFolderId);
            return result.Users.Select(MapUserMember).ToList();
        }

        public Task AddFileMemberAsync(string filePath, string email, string accessLevel = "viewer", CancellationToken cancellationToken = default) =>
            RetryAsync(_ => AddFileMemberCoreAsync(filePath, email, accessLevel), cancellationToken);

        private async Task AddFileMemberCoreAsync(string filePath, string email, string accessLevel = "viewer")
        {
            var level = ParseAccessLevel(accessLevel);
            var member = new MemberSelector.Email(email);
            await _client.Sharing.AddFileMemberAsync(NormalizePath(filePath),
                new MemberSelector[] { member }, accessLevel: level);
        }

        public Task RemoveFileMemberAsync(string filePath, string email, CancellationToken cancellationToken = default) =>
            RetryAsync(_ => _client.Sharing.RemoveFileMember2Async(NormalizePath(filePath),
                new MemberSelector.Email(email)), cancellationToken);

        public Task<List<DropboxMember>> ListFileMembersAsync(string filePath, CancellationToken cancellationToken = default) =>
            RetryAsync(_ => ListFileMembersCoreAsync(filePath), cancellationToken);

        private async Task<List<DropboxMember>> ListFileMembersCoreAsync(string filePath)
        {
            var result = await _client.Sharing.ListFileMembersAsync(NormalizePath(filePath));
            return result.Users.Select(MapUserMember).ToList();
        }

        private static AccessLevel ParseAccessLevel(string level)
        {
            AccessLevel result = level.ToLowerInvariant() switch
            {
                "editor" => AccessLevel.Editor.Instance,
                "viewer_no_comment" => AccessLevel.ViewerNoComment.Instance,
                _ => AccessLevel.Viewer.Instance
            };
            return result;
        }

        private static DropboxMember MapUserMember(UserMembershipInfo u) => new()
        {
            AccountId = u.User.AccountId,
            Email = u.User.Email,
            DisplayName = u.User.DisplayName,
            AccessLevel = u.AccessType?.ToString() ?? "unknown",
            IsInherited = u.IsInherited
        };

        #endregion

        #region Users

        public virtual Task<DropboxAccount> GetCurrentAccountAsync(CancellationToken cancellationToken = default) =>
            RetryAsync(_ => GetCurrentAccountCoreAsync(), cancellationToken);

        private async Task<DropboxAccount> GetCurrentAccountCoreAsync()
        {
            var a = await _client.Users.GetCurrentAccountAsync();
            return new DropboxAccount
            {
                AccountId = a.AccountId,
                DisplayName = a.Name.DisplayName,
                Email = a.Email,
                EmailVerified = a.EmailVerified,
                ProfilePhotoUrl = a.ProfilePhotoUrl ?? "",
                Country = a.Country ?? "",
                Locale = a.Locale ?? "",
                AccountType = a.AccountType?.ToString() ?? "unknown",
                ReferralLink = a.ReferralLink ?? "",
                IsPaired = a.IsPaired
            };
        }

        public Task<DropboxAccount> GetAccountAsync(string accountId, CancellationToken cancellationToken = default) =>
            RetryAsync(_ => GetAccountCoreAsync(accountId), cancellationToken);

        private async Task<DropboxAccount> GetAccountCoreAsync(string accountId)
        {
            var a = await _client.Users.GetAccountAsync(accountId);
            return new DropboxAccount
            {
                AccountId = a.AccountId,
                DisplayName = a.Name.DisplayName,
                Email = a.Email,
                EmailVerified = a.EmailVerified,
                ProfilePhotoUrl = a.ProfilePhotoUrl ?? ""
            };
        }

        public Task<DropboxSpaceUsage> GetSpaceUsageAsync(CancellationToken cancellationToken = default) =>
            RetryAsync(_ => GetSpaceUsageCoreAsync(), cancellationToken);

        private async Task<DropboxSpaceUsage> GetSpaceUsageCoreAsync()
        {
            var usage = await _client.Users.GetSpaceUsageAsync();
            ulong allocated = 0; string label = "";
            if (usage.Allocation.IsIndividual)
            { allocated = usage.Allocation.AsIndividual.Value.Allocated; label = "Individual"; }
            else if (usage.Allocation.IsTeam)
            { allocated = usage.Allocation.AsTeam.Value.Allocated; label = "Team"; }
            return new DropboxSpaceUsage { Used = usage.Used, Allocated = allocated, AllocationLabel = label };
        }

        #endregion

        #region Batch Operations

        public virtual Task<DropboxBatchRelocationResult> CopyBatchAsync(IEnumerable<(string from, string to)> entries, CancellationToken cancellationToken = default) =>
            RetryAsync(_ => CopyBatchCoreAsync(entries), cancellationToken);

        private async Task<DropboxBatchRelocationResult> CopyBatchCoreAsync(IEnumerable<(string from, string to)> entries)
        {
            var paths = entries.Select(e => new RelocationPath(NormalizePath(e.from), NormalizePath(e.to))).ToList();
            var result = await _client.Files.CopyBatchV2Async(paths);
            if (result.IsAsyncJobId)
                return await PollRelocationBatchAsync(result.AsAsyncJobId.Value,
                    id => _client.Files.CopyBatchCheckV2Async(id));
            // The batch completed synchronously: map its entries directly instead of
            // dropping them (which previously reported every item as missing output).
            return MapRelocationEntries(result.AsComplete.Value.Entries);
        }

        public virtual Task<DropboxBatchRelocationResult> MoveBatchAsync(IEnumerable<(string from, string to)> entries, CancellationToken cancellationToken = default) =>
            RetryAsync(_ => MoveBatchCoreAsync(entries), cancellationToken);

        private async Task<DropboxBatchRelocationResult> MoveBatchCoreAsync(IEnumerable<(string from, string to)> entries)
        {
            var paths = entries.Select(e => new RelocationPath(NormalizePath(e.from), NormalizePath(e.to))).ToList();
            var result = await _client.Files.MoveBatchV2Async(paths);
            if (result.IsAsyncJobId)
                return await PollRelocationBatchAsync(result.AsAsyncJobId.Value,
                    id => _client.Files.MoveBatchCheckV2Async(id));
            return MapRelocationEntries(result.AsComplete.Value.Entries);
        }

        /// <summary>
        /// The maximum number of entries Dropbox's <c>files/delete_batch</c> endpoint
        /// accepts in a single request. Callers must chunk larger inputs.
        /// </summary>
        public const int MaxDeleteBatchSize = 1000;

        /// <summary>
        /// Deletes the given paths in a single batch and returns the entries the
        /// server could not delete. An empty list means every path was deleted.
        /// <paramref name="onItemsProcessed"/> is invoked (off the calling thread) each
        /// time a subset of the batch reaches a terminal state -- whether the path was
        /// deleted or was already gone (a permanent "not found") -- including the
        /// partial results between transient-lock retries, so callers can show progress
        /// that advances steadily instead of only when the whole batch finishes. Paths
        /// that were already deleted by a prior run still count as processed, so the
        /// bar climbs quickly through manifest regions that are already clear.
        /// </summary>
        public virtual Task<IReadOnlyList<DropboxBatchDeleteError>> DeleteBatchAsync(
            IEnumerable<string> paths, Action<int>? onItemsProcessed = null,
            CancellationToken cancellationToken = default) =>
            RetryAsync(ct => DeleteBatchCoreAsync(paths, onItemsProcessed, ct), cancellationToken);

        /// <summary>
        /// Deletes many chunks of paths, running up to <paramref name="maxConcurrency"/>
        /// <c>delete_batch</c> jobs at once. Because each batch is processed
        /// asynchronously server-side (and polled), overlapping several in-flight
        /// jobs multiplies throughput. Each chunk should contain at most
        /// <see cref="MaxDeleteBatchSize"/> paths. Returns the aggregated per-entry
        /// failures across all chunks; an empty list means every path was deleted.
        /// <paramref name="onChunkCompleted"/> is invoked (off the calling thread)
        /// after each chunk finishes, with that chunk's entry count, so callers can
        /// report incremental progress. <paramref name="onItemsProcessed"/> fires more
        /// finely -- each time a subset of a chunk reaches a terminal state (deleted or
        /// already gone), including partial results between transient-lock retries -- so
        /// a progress bar can climb steadily through the multi-minute server-side wait
        /// instead of only stepping when a whole chunk completes.
        /// </summary>
        public virtual async Task<IReadOnlyList<DropboxBatchDeleteError>> DeleteBatchesAsync(
            IReadOnlyList<IReadOnlyList<string>> chunks,
            int maxConcurrency,
            Action<int>? onChunkCompleted = null,
            Action<int>? onItemsProcessed = null,
            CancellationToken cancellationToken = default)
        {
            if (chunks is null) throw new ArgumentNullException(nameof(chunks));
            if (chunks.Count == 0) return Array.Empty<DropboxBatchDeleteError>();
            if (maxConcurrency < 1) maxConcurrency = 1;

            using var gate = new SemaphoreSlim(maxConcurrency);
            var failures = new List<DropboxBatchDeleteError>();
            var failuresLock = new object();
            var tasks = new List<Task>(chunks.Count);

            try
            {
                foreach (var chunk in chunks)
                {
                    await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
                    var local = chunk;
                    tasks.Add(Task.Run(async () =>
                    {
                        try
                        {
                            var chunkFailures = await DeleteBatchAsync(local, onItemsProcessed, cancellationToken).ConfigureAwait(false);
                            if (chunkFailures.Count > 0)
                            {
                                lock (failuresLock) { failures.AddRange(chunkFailures); }
                            }
                            onChunkCompleted?.Invoke(local.Count);
                        }
                        catch (OperationCanceledException)
                        {
                            throw;
                        }
                        catch (Exception ex)
                        {
                            // A whole-chunk error must never abort the other in-flight chunks
                            // or lose this chunk's paths. Convert it to per-path failures with
                            // the real paths so the caller records and re-queues them.
                            lock (failuresLock)
                            {
                                foreach (var path in local)
                                {
                                    failures.Add(new DropboxBatchDeleteError(path, ex.Message));
                                }
                            }
                            onChunkCompleted?.Invoke(local.Count);
                        }
                        finally
                        {
                            gate.Release();
                        }
                    }, cancellationToken));
                }
            }
            finally
            {
                // Even if WaitAsync throws on cancellation mid-loop, await the chunk
                // tasks already started so they are not orphaned as unobserved
                // fire-and-forget operations that could overlap a caller's retry.
                try { await Task.WhenAll(tasks).ConfigureAwait(false); }
                catch (OperationCanceledException) { /* expected when cancelled */ }
            }

            return failures;
        }

        /// <summary>
        /// Maximum number of times a single delete batch will re-submit the entries
        /// that failed with the transient <c>too_many_write_operations</c> lock
        /// error before giving up and reporting them as failures. Sized so a chunk
        /// keeps clearing in-run through a sustained throttling spell instead of
        /// spilling to the failed sidecar and churning across runs.
        /// </summary>
        private const int MaxTransientDeleteAttempts = 10;

        /// <summary>Upper bound on a single transient-retry wait, so a pathological
        /// server-supplied <c>Retry-After</c> can never stall the window for minutes.</summary>
        private static readonly TimeSpan MaxTransientDeleteWait = TimeSpan.FromSeconds(60);

        private static readonly Random _deleteJitter = new Random();

        private Task<IReadOnlyList<DropboxBatchDeleteError>> DeleteBatchCoreAsync(
            IEnumerable<string> paths, Action<int>? onItemsProcessed, CancellationToken cancellationToken)
        {
            var normalized = paths.Select(NormalizePath).ToList();
            return RetryTransientDeletesAsync(
                normalized, SubmitDeleteBatchAttemptAsync, SystemDelay.Instance, onItemsProcessed, cancellationToken);
        }

        /// <summary>Outcome of one delete-batch submission: final failures plus the
        /// paths that hit the transient lock error and are worth re-submitting.</summary>
        internal readonly struct DeleteAttemptResult
        {
            public DeleteAttemptResult(
                IReadOnlyList<DropboxBatchDeleteError> permanentFailures,
                IReadOnlyList<string> transientPaths,
                string? transientReason = null,
                TimeSpan? retryAfter = null)
            {
                PermanentFailures = permanentFailures;
                TransientPaths = transientPaths;
                TransientReason = transientReason;
                RetryAfter = retryAfter;
            }

            public IReadOnlyList<DropboxBatchDeleteError> PermanentFailures { get; }
            public IReadOnlyList<string> TransientPaths { get; }

            /// <summary>
            /// Why <see cref="TransientPaths"/> are being retried (e.g. the namespace
            /// lock error or a whole-job failure). Used to label them accurately if
            /// they exhaust the retry budget. Null falls back to the lock-error label.
            /// </summary>
            public string? TransientReason { get; }

            /// <summary>
            /// Server-supplied <c>Retry-After</c> hint (from a rate-limit response) for
            /// the contended paths, when one was provided. The retry loop waits at least
            /// this long before re-submitting so a 429 honors Dropbox's pacing instead of
            /// a blind exponential guess. Null means use the default backoff.
            /// </summary>
            public TimeSpan? RetryAfter { get; }
        }

        /// <summary>
        /// Re-submits the paths that fail with the transient
        /// <c>too_many_write_operations</c> lock error (caused by overlapping writes
        /// on the namespace) with exponential backoff, up to
        /// <see cref="MaxTransientDeleteAttempts"/> times. Paths still contended after
        /// the budget are returned as failures so the caller re-queues them on the
        /// next run -- no special action required. Permanent failures pass straight
        /// through. <paramref name="onItemsProcessed"/> is invoked after every attempt
        /// with the number of paths that reached a terminal state in that attempt --
        /// both successful deletes and permanent failures such as already-gone paths --
        /// so callers see progress climb as the batch resolves rather than waiting for
        /// the whole batch. Only the transient remainder being retried is withheld.
        /// </summary>
        internal async Task<IReadOnlyList<DropboxBatchDeleteError>> RetryTransientDeletesAsync(
            IReadOnlyList<string> paths,
            Func<IReadOnlyList<string>, CancellationToken, Task<DeleteAttemptResult>> submitAttempt,
            IDelay delay,
            Action<int>? onItemsProcessed,
            CancellationToken cancellationToken)
        {
            var remaining = paths;
            var permanentFailures = new List<DropboxBatchDeleteError>();
            string transientReason = "too_many_write_operations";

            for (int attempt = 1; remaining.Count > 0; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int before = remaining.Count;
                var outcome = await submitAttempt(remaining, cancellationToken).ConfigureAwait(false);
                permanentFailures.AddRange(outcome.PermanentFailures);
                if (outcome.TransientReason != null) transientReason = outcome.TransientReason;

                int resolved = before - outcome.TransientPaths.Count;
                if (resolved > 0) onItemsProcessed?.Invoke(resolved);

                if (outcome.TransientPaths.Count == 0) return permanentFailures;

                if (attempt >= MaxTransientDeleteAttempts)
                {
                    foreach (var path in outcome.TransientPaths)
                    {
                        permanentFailures.Add(new DropboxBatchDeleteError(path, transientReason));
                    }
                    return permanentFailures;
                }

                var wait = TransientDeleteWait(attempt, outcome.RetryAfter);
                _rateLimitNotifier?.OnRateLimited(attempt, wait, wait, transientReason);
                await delay.DelayAsync(wait, cancellationToken).ConfigureAwait(false);
                remaining = outcome.TransientPaths;
            }

            return permanentFailures;
        }

        private async Task<DeleteAttemptResult> SubmitDeleteBatchAttemptAsync(
            IReadOnlyList<string> paths, CancellationToken cancellationToken)
        {
            var entries = paths.Select(p => new DeleteArg(p)).ToList();

            DeleteBatchLaunch launch;
            try
            {
                launch = await _client.Files.DeleteBatchAsync(entries);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (IsTransientTransportException(ex))
            {
                // A network/transport blip (e.g. HttpRequestException, timeout) on the
                // submit call applied nothing. Re-queue the whole chunk on the transient
                // path so it is retried with backoff rather than permanently failed. Carry
                // any server Retry-After so a 429 waits exactly as long as Dropbox asks.
                return new DeleteAttemptResult(
                    Array.Empty<DropboxBatchDeleteError>(), paths, DescribeTransportError(ex),
                    GetRetryAfterHint(ex));
            }

            DeleteBatchResult result;
            if (launch.IsAsyncJobId)
            {
                try
                {
                    result = await PollDeleteBatchAsync(launch.AsAsyncJobId.Value, cancellationToken);
                }
                catch (DeleteBatchJobFailedException ex)
                {
                    // The whole delete_batch job reported Failed, meaning no entries were
                    // applied. Re-submit the entire chunk on the transient path so it is
                    // retried with exponential backoff instead of aborting the window and
                    // losing the paths. Idempotent: any already-removed entries simply come
                    // back as benign "not found" on a later attempt. Carry the REAL Dropbox
                    // reason so an exhausted retry is labeled accurately (contention vs other).
                    return new DeleteAttemptResult(
                        Array.Empty<DropboxBatchDeleteError>(), paths, ex.Reason);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex) when (IsTransientTransportException(ex))
                {
                    // A transient transport fault while polling the job status -- the job may
                    // still be running server-side, but re-submitting the (idempotent) chunk
                    // on the transient path is safe and lets the backoff loop recover instead
                    // of failing the window. Carry any server Retry-After hint.
                    return new DeleteAttemptResult(
                        Array.Empty<DropboxBatchDeleteError>(), paths, DescribeTransportError(ex),
                        GetRetryAfterHint(ex));
                }
            }
            else if (launch.IsComplete)
            {
                result = launch.AsComplete.Value;
            }
            else
            {
                return new DeleteAttemptResult(Array.Empty<DropboxBatchDeleteError>(), Array.Empty<string>());
            }

            var permanent = new List<DropboxBatchDeleteError>();
            var transient = new List<string>();
            for (int i = 0; i < result.Entries.Count; i++)
            {
                if (!result.Entries[i].IsFailure) continue;
                var path = i < paths.Count ? paths[i] : string.Empty;
                var error = result.Entries[i].AsFailure.Value;
                var reason = DescribeDeleteError(error);
                if (error.IsTooManyWriteOperations || IsTransientDeleteReason(reason))
                {
                    transient.Add(path);
                }
                else
                {
                    permanent.Add(new DropboxBatchDeleteError(path, reason));
                }
            }

            return new DeleteAttemptResult(permanent, transient);
        }

        /// <summary>
        /// Classifies a delete failure reason string as transient (worth retrying
        /// in-run with backoff) vs permanent. Covers namespace write contention,
        /// Dropbox server-side internal errors, rate limiting, and transport timeouts.
        /// </summary>
        internal static bool IsTransientDeleteReason(string? reason)
        {
            if (string.IsNullOrEmpty(reason)) return false;
            string r = reason!.ToLowerInvariant();
            return r.Contains("too_many_write_operations")
                || r.Contains("internal_error")
                || r.Contains("too_many_requests")
                || r.Contains("rate_limit")
                || r.Contains("timed out")
                || r.Contains("timeout")
                || r.Contains("temporarily")
                || r.Contains("service unavailable")
                || r.Contains("an error occurred while sending the request");
        }

        /// <summary>
        /// True when an exception thrown by a delete network call is a transient
        /// transport fault (network blip, timeout, server 5xx/rate limit) that
        /// should be retried, rather than a deterministic failure. User-initiated
        /// cancellation is never transient.
        /// </summary>
        internal static bool IsTransientTransportException(Exception ex)
        {
            if (ex is null) return false;
            if (ex is OperationCanceledException) return false;
            if (ex is System.Net.Http.HttpRequestException) return true;
            if (ex is TimeoutException) return true;
            if (ex is System.IO.IOException) return true;
            string name = ex.GetType().Name;
            if (name.Contains("RateLimit") || name.Contains("Retry") || name.Contains("ServiceUnavailable"))
            {
                return true;
            }
            return IsTransientDeleteReason(ex.Message);
        }

        /// <summary>Builds a stable transient reason label from a transport exception.</summary>
        private static string DescribeTransportError(Exception ex)
        {
            string m = CleanReason(ex?.Message);
            return m == "unknown error" ? "transient transport error" : m;
        }

        /// <summary>Exponential backoff with jitter for retrying contended deletes.</summary>
        private static TimeSpan TransientDeleteBackoff(int attempt)
        {
            double seconds = Math.Min(20, Math.Pow(2, attempt - 1));   // 1,2,4,8,16,20...
            int jitterMs;
            lock (_deleteJitter) { jitterMs = _deleteJitter.Next(0, 1000); }
            return TimeSpan.FromSeconds(seconds) + TimeSpan.FromMilliseconds(jitterMs);
        }

        /// <summary>
        /// Resolves the wait before the next transient delete re-submission: the larger
        /// of the exponential backoff and any server-supplied <c>Retry-After</c>, capped
        /// at <see cref="MaxTransientDeleteWait"/>. Honoring <c>Retry-After</c> means a 429
        /// waits exactly as long as Dropbox asked instead of a blind exponential guess.
        /// </summary>
        private static TimeSpan TransientDeleteWait(int attempt, TimeSpan? retryAfter)
        {
            var wait = TransientDeleteBackoff(attempt);
            if (retryAfter is TimeSpan ra && ra > wait) wait = ra;
            if (wait > MaxTransientDeleteWait) wait = MaxTransientDeleteWait;
            return wait;
        }

        /// <summary>
        /// Extracts a server <c>Retry-After</c> hint from a rate-limit exception, when the
        /// transport surfaced one. Returns null for non-rate-limit faults so the caller
        /// falls back to exponential backoff.
        /// </summary>
        private static TimeSpan? GetRetryAfterHint(Exception ex)
        {
            if (ex is RateLimitException rl && rl.RetryAfter > 0)
            {
                return TimeSpan.FromSeconds(rl.RetryAfter);
            }
            return null;
        }

        private async Task<DeleteBatchResult> PollDeleteBatchAsync(string jobId, CancellationToken cancellationToken)
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await Task.Delay(500, cancellationToken).ConfigureAwait(false);
                var check = await _client.Files.DeleteBatchCheckAsync(jobId);
                if (check.IsComplete) return check.AsComplete.Value;
                if (check.IsFailed) throw new DeleteBatchJobFailedException(DescribeDeleteBatchError(check.AsFailed.Value));
            }
        }

        /// <summary>
        /// Translates a job-level <see cref="DeleteBatchError"/> (the reason a whole
        /// <c>delete_batch</c> job ended in the Failed state) into a stable reason string
        /// so the operator can tell write contention from a genuine fault.
        /// </summary>
        internal static string DescribeDeleteBatchError(DeleteBatchError error)
        {
            if (error is null) return "batch delete job failed";
            if (error.IsTooManyWriteOperations) return "too_many_write_operations";
            return "batch delete job failed";
        }

        /// <summary>
        /// Raised when a Dropbox <c>delete_batch</c> job finishes in the Failed state
        /// (an all-or-nothing job failure where no entries were applied). Caught by the
        /// submit path, which re-queues the whole chunk for transient retry rather than
        /// aborting the in-flight delete window. <see cref="Reason"/> carries the real
        /// Dropbox cause so an exhausted retry is labeled accurately.
        /// </summary>
        private sealed class DeleteBatchJobFailedException : Exception
        {
            public DeleteBatchJobFailedException(string reason)
                : base(reason) => Reason = reason;

            public string Reason { get; }
        }

        private static string DescribeDeleteError(DeleteError error)
        {
            if (error.IsPathLookup && error.AsPathLookup.Value.IsNotFound)
                return "path not found";
            return CleanReason(error.ToString());
        }

        /// <summary>
        /// Normalizes a raw SDK reason string for display: trims whitespace and the
        /// trailing <c>/</c> and <c>.</c> the Dropbox union <c>ToString()</c> appends for
        /// tags with no inner value (e.g. <c>internal_error/</c> -> <c>internal_error</c>,
        /// <c>too_many_requests/..</c> -> <c>too_many_requests</c>).
        /// </summary>
        internal static string CleanReason(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return "unknown error";
            string r = raw!.Trim();
            while (r.Length > 0 &&
                   (r.EndsWith("/", StringComparison.Ordinal) || r.EndsWith(".", StringComparison.Ordinal)))
            {
                r = r.Substring(0, r.Length - 1);
            }
            r = r.Trim();
            return r.Length == 0 ? "unknown error" : r;
        }

        private async Task<DropboxBatchRelocationResult> PollRelocationBatchAsync(
            string jobId, Func<string, Task<RelocationBatchV2JobStatus>> checkFunc)
        {
            while (true)
            {
                await Task.Delay(500);
                var status = await checkFunc(jobId);
                if (status.IsComplete)
                    return MapRelocationEntries(status.AsComplete.Value.Entries);
            }
        }

        /// <summary>
        /// Maps the per-entry results of a Dropbox batch relocation into successes
        /// and failures so partial failures surface as errors rather than being
        /// silently dropped from the returned items.
        /// </summary>
        private static DropboxBatchRelocationResult MapRelocationEntries(
            IEnumerable<RelocationBatchResultEntry> entries)
        {
            var items = new List<DropboxItem>();
            var failures = new List<DropboxBatchRelocationError>();
            foreach (var entry in entries)
            {
                if (entry.IsSuccess)
                    items.Add(MapMetadataToItem(entry.AsSuccess.Value));
                else if (entry.IsFailure)
                    failures.Add(new DropboxBatchRelocationError(DescribeRelocationError(entry.AsFailure.Value)));
                else
                    failures.Add(new DropboxBatchRelocationError("Unknown relocation result."));
            }
            return new DropboxBatchRelocationResult(items, failures);
        }

        /// <summary>Produces a short, human-readable description of a batch relocation failure.</summary>
        private static string DescribeRelocationError(RelocationBatchErrorEntry error)
        {
            if (error.IsRelocationError)
                return error.AsRelocationError.Value.ToString() ?? "relocation error";
            if (error.IsInternalError)
                return "internal error";
            if (error.IsTooManyWriteOperations)
                return "too many write operations";
            return error.ToString() ?? "unknown error";
        }

        #endregion

        #region Paper

        public Task<string> CreatePaperDocAsync(string path, byte[] content, string importFormat = "html", CancellationToken cancellationToken = default) =>
            RetryAsync(_ => CreatePaperDocCoreAsync(path, content, importFormat), cancellationToken);

        private async Task<string> CreatePaperDocCoreAsync(string path, byte[] content, string importFormat = "html")
        {
            var format = ParseImportFormat(importFormat);
            using var stream = new MemoryStream(content);
            var result = await _client.Files.PaperCreateAsync(NormalizePath(path), format, body: stream);
            return result.Url;
        }

        public Task<string> UpdatePaperDocAsync(string path, byte[] content,
            string importFormat = "html", string docUpdatePolicy = "overwrite", CancellationToken cancellationToken = default) =>
            RetryAsync(_ => UpdatePaperDocCoreAsync(path, content, importFormat, docUpdatePolicy), cancellationToken);

        private async Task<string> UpdatePaperDocCoreAsync(string path, byte[] content,
            string importFormat = "html", string docUpdatePolicy = "overwrite")
        {
            var format = ParseImportFormat(importFormat);
            PaperDocUpdatePolicy policy = docUpdatePolicy.ToLowerInvariant() switch
            {
                "prepend" => PaperDocUpdatePolicy.Prepend.Instance,
                "append" => PaperDocUpdatePolicy.Append.Instance,
                _ => PaperDocUpdatePolicy.Overwrite.Instance
            };
            using var stream = new MemoryStream(content);
            var result = await _client.Files.PaperUpdateAsync(NormalizePath(path), format, policy, body: stream);
            return $"revision:{result.PaperRevision}";
        }

        private static ImportFormat ParseImportFormat(string fmt)
        {
            ImportFormat format = fmt.ToLowerInvariant() switch
            {
                "markdown" => ImportFormat.Markdown.Instance,
                "plain_text" => ImportFormat.PlainText.Instance,
                _ => ImportFormat.Html.Instance
            };
            return format;
        }

        #endregion

        #region Mapping Helpers

        private static DropboxItem MapMetadataToItem(Metadata metadata)
        {
            var item = new DropboxItem
            {
                Name = metadata.Name,
                Path = metadata.PathDisplay ?? metadata.PathLower ?? "",
                IsFolder = metadata.IsFolder,
                IsDeleted = metadata.IsDeleted,
                ParentSharedFolderId = metadata.ParentSharedFolderId ?? ""
            };

            if (metadata.IsFile && metadata.AsFile is FileMetadata file)
            {
                item.Id = file.Id ?? "";
                item.Length = file.Size;
                item.ServerModified = file.ServerModified;
                item.ClientModified = file.ClientModified;
                item.Rev = file.Rev ?? "";
                item.ContentHash = file.ContentHash ?? "";
                item.HasExplicitSharedMembers = file.HasExplicitSharedMembers ?? false;
                item.IsDownloadable = file.IsDownloadable;
                item.SymlinkTarget = file.SymlinkInfo?.Target ?? "";
                item.MediaInfoTag = MapMediaInfoTag(file.MediaInfo);
            }
            else if (metadata.IsFolder && metadata.AsFolder is FolderMetadata folder)
            {
                item.Id = folder.Id ?? "";
                item.SharedFolderId = folder.SharedFolderId ?? "";
            }
            return item;
        }

        /// <summary>
        /// Reduces a Dropbox <see cref="MediaInfo"/> to a short tag:
        /// <c>pending</c>, <c>photo</c>, <c>video</c>, <c>metadata</c>, or empty
        /// when no media info is present.
        /// </summary>
        private static string MapMediaInfoTag(MediaInfo? mediaInfo)
        {
            if (mediaInfo == null) return "";
            if (mediaInfo.IsPending) return "pending";
            if (!mediaInfo.IsMetadata) return "";
            var value = mediaInfo.AsMetadata?.Value;
            return value switch
            {
                PhotoMetadata => "photo",
                VideoMetadata => "video",
                _ => "metadata"
            };
        }

        private static DropboxSharedLink MapSharedLink(SharedLinkMetadata link) => new()
        {
            Url = link.Url,
            Path = link.PathLower ?? "",
            Name = link.Name,
            Id = link.Id ?? "",
            Expires = link.Expires,
            Visibility = link.LinkPermissions?.ResolvedVisibility?.ToString() ?? ""
        };

        private static DropboxSharedFolder MapSharedFolder(SharedFolderMetadata folder) => new()
        {
            SharedFolderId = folder.SharedFolderId,
            Name = folder.Name,
            PathLower = folder.PathLower ?? "",
            AccessType = folder.AccessType?.ToString() ?? "unknown",
            IsInsideTeamFolder = folder.IsInsideTeamFolder,
            IsTeamFolder = folder.IsTeamFolder
        };

        #endregion

        public void Dispose()
        {
            // Intentionally do NOT dispose _client. The Dropbox.Api SDK
            // (v7.0.0) uses a static shared DefaultHttpClient internally
            // when no HttpClient is supplied, and DropboxClient.Dispose()
            // disposes that static singleton. Disposing here would break
            // every subsequent DropboxClient instance in the process.
            // The DropboxClient is small and the process exits soon
            // afterwards in normal usage, so the leak is acceptable.
            GC.SuppressFinalize(this);
        }
    }
}