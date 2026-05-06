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
using DbxProvider.Models;

namespace DbxProvider.Services
{
    /// <summary>Comprehensive wrapper around the Dropbox API v2.</summary>
    public class DropboxServiceClient : IDisposable
    {
        private readonly DropboxClient _client;
        private const int UploadSessionChunkSize = 8 * 1024 * 1024;
        private const long UploadSessionThreshold = 150L * 1024 * 1024;

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
            if (!path.StartsWith("/"))
                path = "/" + path;
            return path.TrimEnd('/');
        }

        #endregion

        #region Files - List / Get Metadata

        public Task<List<DropboxItem>> ListFolderAsync(string path, bool recursive = false, bool includeDeleted = false, CancellationToken cancellationToken = default) =>
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
        public Task<(List<DropboxItem> Items, string Cursor)> ListFolderWithCursorAsync(string path, bool recursive = false, bool includeDeleted = false, CancellationToken cancellationToken = default) =>
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
        public Task<ListFolderDelta> ListFolderContinueRawAsync(string cursor, CancellationToken cancellationToken = default) =>
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

        public Task<DropboxItem> GetMetadataAsync(string path, bool includeDeleted = false, CancellationToken cancellationToken = default) =>
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

        public Task<bool> ItemExistsAsync(string path, CancellationToken cancellationToken = default) =>
            RetryAsync(_ => ItemExistsCoreAsync(path), cancellationToken);

        private async Task<bool> ItemExistsCoreAsync(string path)
{
            try { await GetMetadataAsync(path); return true; }
            catch (ApiException<GetMetadataError>) { return false; }
        }

        #endregion

        #region Files - Download / Upload

        public Task<(Stream Content, DropboxItem Metadata)> DownloadAsync(string path, CancellationToken cancellationToken = default) =>
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

        public Task<DropboxItem> UploadAsync(string path, Stream content, WriteMode? mode = null, CancellationToken cancellationToken = default) =>
            RetryAsync(_ => UploadCoreAsync(path, content, mode), cancellationToken);

        private async Task<DropboxItem> UploadCoreAsync(string path, Stream content, WriteMode? mode = null)
{
            var dbxPath = NormalizePath(path);
            mode ??= WriteMode.Overwrite.Instance;
            if (content.CanSeek && content.Length <= UploadSessionThreshold)
            {
                var metadata = await _client.Files.UploadAsync(dbxPath, mode: mode, body: content);
                return MapMetadataToItem(metadata);
            }
            return await UploadSessionAsync(dbxPath, content, mode);
        }

        private async Task<DropboxItem> UploadSessionAsync(string path, Stream content, WriteMode mode)
        {
            var buffer = new byte[UploadSessionChunkSize];
            int bytesRead = await content.ReadAsync(buffer, 0, UploadSessionChunkSize);

            using var firstChunk = new MemoryStream(buffer, 0, bytesRead);
            var session = await _client.Files.UploadSessionStartAsync(body: firstChunk);
            ulong offset = (ulong)bytesRead;

            while (true)
            {
                bytesRead = await content.ReadAsync(buffer, 0, UploadSessionChunkSize);
                if (bytesRead <= 0) break;

                bool isLast = (content.CanSeek && content.Position >= content.Length) || bytesRead < UploadSessionChunkSize;

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

        public Task DeleteAsync(string path, bool permanent = false, CancellationToken cancellationToken = default) =>
            RetryAsync(_ => DeleteCoreAsync(path, permanent), cancellationToken);

        private async Task DeleteCoreAsync(string path, bool permanent = false)
{
            var dbxPath = NormalizePath(path);
            if (permanent)
                await _client.Files.PermanentlyDeleteAsync(dbxPath);
            else
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
        /// <see cref="System.Management.Automation.WildcardPattern"/> to enforce
        /// true PowerShell wildcard semantics.
        /// </summary>
        public Task<List<DropboxItem>> SearchByFilenameAsync(string pattern,
            string path = "", int maxResults = 1000, CancellationToken cancellationToken = default) =>
            RetryAsync(_ => SearchByFilenameCoreAsync(pattern, path, maxResults), cancellationToken);

        private async Task<List<DropboxItem>> SearchByFilenameCoreAsync(string pattern,
            string path = "", int maxResults = 1000)
{
            var wildcard = new System.Management.Automation.WildcardPattern(
                pattern, System.Management.Automation.WildcardOptions.IgnoreCase);

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

            // Some queries (e.g. just an extension token) are too generic for
            // search_v2. Dropbox requires a non-empty query, so when we only
            // have an extension filter we still must pass something. Use the
            // extension as the token in that case.
            if (string.IsNullOrEmpty(query))
            {
                query = extension!;
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

        public Task<List<DropboxRevision>> ListRevisionsAsync(string path, int limit = 10, CancellationToken cancellationToken = default) =>
            RetryAsync(_ => ListRevisionsCoreAsync(path, limit), cancellationToken);

        private async Task<List<DropboxRevision>> ListRevisionsCoreAsync(string path, int limit = 10)
{
            var result = await _client.Files.ListRevisionsAsync(NormalizePath(path), limit: (ulong)limit);
            return result.Entries.Select(e => new DropboxRevision
            {
                Name = e.Name, Path = e.PathDisplay ?? e.PathLower ?? "",
                Rev = e.Rev, Size = e.Size,
                ServerModified = e.ServerModified, ClientModified = e.ClientModified,
                ContentHash = e.ContentHash ?? "", IsDeleted = result.IsDeleted
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
                Size = result.Response.FileMetadata?.Size ?? 0,
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

        #region Files - Locks

        public Task<List<DropboxItem>> LockFilesAsync(string path, CancellationToken cancellationToken = default) =>
            LockFilesAsync(new[] { path }, cancellationToken);

        public Task<List<DropboxItem>> LockFilesAsync(string[] paths, CancellationToken cancellationToken = default) =>
            RetryAsync(_ => LockFilesCoreAsync(paths), cancellationToken);

        private async Task<List<DropboxItem>> LockFilesCoreAsync(params string[] paths)
{
            var entries = paths.Select(p => new LockFileArg(NormalizePath(p))).ToList();
            var result = await _client.Files.LockFileBatchAsync(entries);
            return result.Entries
                .Where(e => e.IsSuccess)
                .Select(e => MapMetadataToItem(e.AsSuccess.Value.Metadata))
                .ToList();
        }

        public Task<List<DropboxItem>> UnlockFilesAsync(string path, CancellationToken cancellationToken = default) =>
            UnlockFilesAsync(new[] { path }, cancellationToken);

        public Task<List<DropboxItem>> UnlockFilesAsync(string[] paths, CancellationToken cancellationToken = default) =>
            RetryAsync(_ => UnlockFilesCoreAsync(paths), cancellationToken);

        private async Task<List<DropboxItem>> UnlockFilesCoreAsync(params string[] paths)
{
            var entries = paths.Select(p => new UnlockFileArg(NormalizePath(p))).ToList();
            var result = await _client.Files.UnlockFileBatchAsync(entries);
            return result.Entries
                .Where(e => e.IsSuccess)
                .Select(e => MapMetadataToItem(e.AsSuccess.Value.Metadata))
                .ToList();
        }

        public Task<List<DropboxItem>> GetFileLocksAsync(string path, CancellationToken cancellationToken = default) =>
            GetFileLocksAsync(new[] { path }, cancellationToken);

        public Task<List<DropboxItem>> GetFileLocksAsync(string[] paths, CancellationToken cancellationToken = default) =>
            RetryAsync(_ => GetFileLocksCoreAsync(paths), cancellationToken);

        private async Task<List<DropboxItem>> GetFileLocksCoreAsync(params string[] paths)
{
            var entries = paths.Select(p => new LockFileArg(NormalizePath(p))).ToList();
            var result = await _client.Files.GetFileLockBatchAsync(entries);
            return result.Entries
                .Where(e => e.IsSuccess)
                .Select(e => MapMetadataToItem(e.AsSuccess.Value.Metadata))
                .ToList();
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

        public Task<DropboxAccount> GetCurrentAccountAsync(CancellationToken cancellationToken = default) =>
            RetryAsync(_ => GetCurrentAccountCoreAsync(), cancellationToken);

        private async Task<DropboxAccount> GetCurrentAccountCoreAsync()
{
            var a = await _client.Users.GetCurrentAccountAsync();
            return new DropboxAccount
            {
                AccountId = a.AccountId, DisplayName = a.Name.DisplayName,
                Email = a.Email, EmailVerified = a.EmailVerified,
                ProfilePhotoUrl = a.ProfilePhotoUrl ?? "", Country = a.Country ?? "",
                Locale = a.Locale ?? "", AccountType = a.AccountType?.ToString() ?? "unknown",
                ReferralLink = a.ReferralLink ?? "", IsPaired = a.IsPaired
            };
        }

        public Task<DropboxAccount> GetAccountAsync(string accountId, CancellationToken cancellationToken = default) =>
            RetryAsync(_ => GetAccountCoreAsync(accountId), cancellationToken);

        private async Task<DropboxAccount> GetAccountCoreAsync(string accountId)
{
            var a = await _client.Users.GetAccountAsync(accountId);
            return new DropboxAccount
            {
                AccountId = a.AccountId, DisplayName = a.Name.DisplayName,
                Email = a.Email, EmailVerified = a.EmailVerified,
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

        public Task<List<DropboxItem>> CopyBatchAsync(IEnumerable<(string from, string to)> entries, CancellationToken cancellationToken = default) =>
            RetryAsync(_ => CopyBatchCoreAsync(entries), cancellationToken);

        private async Task<List<DropboxItem>> CopyBatchCoreAsync(IEnumerable<(string from, string to)> entries)
        {
            var paths = entries.Select(e => new RelocationPath(NormalizePath(e.from), NormalizePath(e.to))).ToList();
            var result = await _client.Files.CopyBatchV2Async(paths);
            if (result.IsAsyncJobId)
                return await PollRelocationBatchAsync(result.AsAsyncJobId.Value,
                    id => _client.Files.CopyBatchCheckV2Async(id));
            return new List<DropboxItem>();
        }

        public Task<List<DropboxItem>> MoveBatchAsync(IEnumerable<(string from, string to)> entries, CancellationToken cancellationToken = default) =>
            RetryAsync(_ => MoveBatchCoreAsync(entries), cancellationToken);

        private async Task<List<DropboxItem>> MoveBatchCoreAsync(IEnumerable<(string from, string to)> entries)
        {
            var paths = entries.Select(e => new RelocationPath(NormalizePath(e.from), NormalizePath(e.to))).ToList();
            var result = await _client.Files.MoveBatchV2Async(paths);
            if (result.IsAsyncJobId)
                return await PollRelocationBatchAsync(result.AsAsyncJobId.Value,
                    id => _client.Files.MoveBatchCheckV2Async(id));
            return new List<DropboxItem>();
        }

        public Task DeleteBatchAsync(IEnumerable<string> paths, CancellationToken cancellationToken = default) =>
            RetryAsync(_ => DeleteBatchCoreAsync(paths), cancellationToken);

        private async Task DeleteBatchCoreAsync(IEnumerable<string> paths)
{
            var entries = paths.Select(p => new DeleteArg(NormalizePath(p))).ToList();
            var result = await _client.Files.DeleteBatchAsync(entries);
            if (result.IsAsyncJobId)
            {
                var jobId = result.AsAsyncJobId.Value;
                while (true)
                {
                    await Task.Delay(500);
                    var check = await _client.Files.DeleteBatchCheckAsync(jobId);
                    if (check.IsComplete) break;
                    if (check.IsFailed) throw new Exception("Batch delete failed.");
                }
            }
        }

        private async Task<List<DropboxItem>> PollRelocationBatchAsync(
            string jobId, Func<string, Task<RelocationBatchV2JobStatus>> checkFunc)
        {
            while (true)
            {
                await Task.Delay(500);
                var status = await checkFunc(jobId);
                if (status.IsComplete)
                    return status.AsComplete.Value.Entries
                        .Where(e => e.IsSuccess)
                        .Select(e => MapMetadataToItem(e.AsSuccess.Value))
                        .ToList();
            }
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
                item.Size = file.Size;
                item.ServerModified = file.ServerModified;
                item.ClientModified = file.ClientModified;
                item.Rev = file.Rev ?? "";
                item.ContentHash = file.ContentHash ?? "";
                item.HasExplicitSharedMembers = file.HasExplicitSharedMembers ?? false;
                item.IsDownloadable = file.IsDownloadable;
                item.SymlinkTarget = file.SymlinkInfo?.Target ?? "";
                if (file.FileLockInfo != null)
                {
                    item.LockInfo = new Models.FileLockInfo
                    {
                        IsLockedByMe = file.FileLockInfo.IsLockholder ?? false,
                        Created = file.FileLockInfo.Created
                    };
                }
            }
            else if (metadata.IsFolder && metadata.AsFolder is FolderMetadata folder)
            {
                item.Id = folder.Id ?? "";
                item.SharedFolderId = folder.SharedFolderId ?? "";
            }
            return item;
        }

        private static DropboxSharedLink MapSharedLink(SharedLinkMetadata link) => new()
        {
            Url = link.Url, Path = link.PathLower ?? "", Name = link.Name,
            Id = link.Id ?? "", Expires = link.Expires,
            Visibility = link.LinkPermissions?.ResolvedVisibility?.ToString() ?? ""
        };

        private static DropboxSharedFolder MapSharedFolder(SharedFolderMetadata folder) => new()
        {
            SharedFolderId = folder.SharedFolderId, Name = folder.Name,
            PathLower = folder.PathLower ?? "",
            AccessType = folder.AccessType?.ToString() ?? "unknown",
            IsInsideTeamFolder = folder.IsInsideTeamFolder, IsTeamFolder = folder.IsTeamFolder
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