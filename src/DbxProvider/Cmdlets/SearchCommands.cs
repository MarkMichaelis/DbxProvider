using System;
using System.Linq;
using System.Management.Automation;
using IntelliTect.Dropbox;
using Dropbox.Api.Files;

namespace DbxProvider.Cmdlets
{
    /// <summary>
    /// Searches Dropbox for files and folders by name. By default the search runs
    /// against the local metadata cache (zero Dropbox API calls for the lookup,
    /// exhaustive, and auto-refreshed from the delta cursor first), which is far
    /// faster than crawling the account. The query is matched as a glob when it
    /// contains a wildcard (<c>*</c>, <c>?</c> or <c>[</c>) and as a substring
    /// otherwise. Use <c>-NoCache</c> to fall back to the server-side
    /// <c>search_v2</c> index (which also matches file contents and supports the
    /// server-side category/extension/status filters). Build or refresh the cache
    /// with Build-DropboxCacheAll.ps1.
    /// </summary>
    [Cmdlet(VerbsCommon.Search, "Dropbox")]
    [OutputType(typeof(DropboxItem), typeof(DropboxSearchResult))]
    public class SearchDropboxCommand : DropboxCmdletBase
    {
        /// <summary>Search query. A query containing a wildcard (<c>*</c>, <c>?</c>
        /// or <c>[</c>) is matched as a glob; otherwise it is a substring match.</summary>
        [Parameter(Mandatory = true, Position = 0)]
        [SupportsWildcards()]
        public string Query { get; set; } = string.Empty;

        /// <summary>Dropbox path (or drive path such as <c>Dbx:\Folder</c>) to
        /// search under. Empty (the default) searches the entire account.</summary>
        [Parameter]
        public string Path { get; set; } = string.Empty;

        /// <summary>Search the server-side <c>search_v2</c> index instead of the
        /// local metadata cache. Slower, but matches file contents and honors the
        /// server-side filters (<c>-FileCategory</c>, <c>-FileExtensions</c>,
        /// <c>-FileStatus</c>, <c>-OrderBy</c>, <c>-IncludeHighlights</c>).</summary>
        [Parameter]
        public SwitchParameter NoCache { get; set; }

        /// <summary>Match only zero-byte files (skips folders and non-empty files).
        /// Cache mode only; ignored with <c>-NoCache</c>.</summary>
        [Parameter]
        public SwitchParameter ZeroByteOnly { get; set; }

        /// <summary>Maximum number of server results to return (<c>-NoCache</c> only).
        /// Defaults to 100. Cache mode returns every match.</summary>
        [Parameter]
        public int MaxResults { get; set; } = 100;

        /// <summary>Include match highlights / snippets on each result (<c>-NoCache</c> only).</summary>
        [Parameter]
        public SwitchParameter IncludeHighlights { get; set; }

        /// <summary>Restrict server matching to filenames, skipping file content
        /// indexing (<c>-NoCache</c> only).</summary>
        [Parameter]
        public SwitchParameter FilenameOnly { get; set; }

        /// <summary>Server-side filter on file extensions, e.g. pdf, docx (<c>-NoCache</c> only).</summary>
        [Parameter]
        public string[]? FileExtensions { get; set; }

        /// <summary>Server-side filter on Dropbox file categories (<c>-NoCache</c> only).</summary>
        [Parameter]
        [ValidateSet("Image", "Document", "Pdf", "Spreadsheet", "Presentation",
            "Audio", "Video", "Folder", "Paper", "Others",
            IgnoreCase = true)]
        public string[]? FileCategory { get; set; }

        /// <summary>Search active or deleted files (<c>-NoCache</c> only, default: Active).</summary>
        [Parameter]
        [ValidateSet("Active", "Deleted", IgnoreCase = true)]
        public string FileStatus { get; set; } = "Active";

        /// <summary>Result ordering (<c>-NoCache</c> only, default: Relevance).</summary>
        [Parameter]
        [ValidateSet("Relevance", "LastModifiedTime", IgnoreCase = true)]
        public string OrderBy { get; set; } = "Relevance";

        /// <summary>Routes to the cache search (default) or the server search.</summary>
        protected override void ProcessRecord()
        {
            try
            {
                if (NoCache.IsPresent)
                    SearchServer();
                else
                    SearchCache();
            }
            catch (PipelineStoppedException)
            {
                // A downstream cmdlet stopped the pipeline early (e.g.
                // Select-Object -First) or the user pressed Ctrl+C. This is a
                // cooperative stop, not a search failure: let it propagate so the
                // already-emitted objects are preserved instead of being discarded
                // and turned into a spurious "The pipeline has been stopped" error.
                throw;
            }
            catch (Exception ex)
            {
                WriteError(new ErrorRecord(ex, "SearchFailed",
                    ErrorCategory.ReadError, Query));
            }
        }

        /// <summary>Streams matches straight from the auto-refreshed metadata cache,
        /// emitting each <see cref="DropboxItem"/> as it is found so a broad query
        /// never materializes the whole result set in memory.</summary>
        private void SearchCache()
        {
            var cache = GetRefreshedCache();
            var startPath = StripDrivePrefix(Path);

            if (cache.PersistedCount() == 0)
            {
                WriteWarning(
                    "The metadata cache is empty. Run Build-DropboxCacheAll.ps1 (or " +
                    "Build-DropboxCache) to populate it, or pass -NoCache to search the " +
                    "server-side index instead.");
                return;
            }

            var predicate = BuildNamePredicate(ToNamePattern(Query), ZeroByteOnly.IsPresent);
            int count = 0;
            foreach (var item in cache.EnumerateItems(startPath))
                if (predicate(item))
                {
                    WriteDropboxItem(item);
                    count++;
                }

            WriteVerbose($"Found {count} cached match(es) for '{Query}'.");
        }

        /// <summary>Queries the server-side <c>search_v2</c> index, auto-detecting a
        /// wildcard query to enforce PowerShell glob semantics on the result set.</summary>
        private void SearchServer()
        {
            if (ZeroByteOnly.IsPresent)
                WriteWarning("-ZeroByteOnly is ignored with -NoCache (the server index has no size filter).");

            var service = GetService();

            if (ContainsWildcard(Query))
            {
                var items = service.SearchByFilenameAsync(Query, Path, MaxResults)
                    .GetAwaiter().GetResult();
                foreach (var item in items)
                    WriteObject(new DropboxSearchResult { MatchType = "Filename", Item = item });
                WriteVerbose($"Found {items.Count} server result(s) for wildcard '{Query}'.");
                return;
            }

            var categories = FileCategory?.Select(MapCategory).ToArray();
            var results = Run(ct => service.SearchAsync(
                query: Query,
                path: Path,
                maxResults: MaxResults,
                includeHighlights: IncludeHighlights.IsPresent,
                filenameOnly: FilenameOnly.IsPresent,
                fileExtensions: FileExtensions,
                fileCategories: categories,
                fileStatus: MapStatus(FileStatus),
                orderBy: MapOrderBy(OrderBy), cancellationToken: ct));

            foreach (var result in results)
                WriteObject(result);

            WriteVerbose($"Found {results.Count} server result(s) for '{Query}'.");
        }

        private static Dropbox.Api.Files.FileCategory MapCategory(string name) =>
            name.ToLowerInvariant() switch
            {
                "image" => Dropbox.Api.Files.FileCategory.Image.Instance,
                "document" => Dropbox.Api.Files.FileCategory.Document.Instance,
                "pdf" => Dropbox.Api.Files.FileCategory.Pdf.Instance,
                "spreadsheet" => Dropbox.Api.Files.FileCategory.Spreadsheet.Instance,
                "presentation" => Dropbox.Api.Files.FileCategory.Presentation.Instance,
                "audio" => Dropbox.Api.Files.FileCategory.Audio.Instance,
                "video" => Dropbox.Api.Files.FileCategory.Video.Instance,
                "folder" => Dropbox.Api.Files.FileCategory.Folder.Instance,
                "paper" => Dropbox.Api.Files.FileCategory.Paper.Instance,
                "others" => Dropbox.Api.Files.FileCategory.Others.Instance,
                _ => throw new ArgumentException($"Unknown FileCategory: {name}"),
            };

        private static Dropbox.Api.Files.FileStatus MapStatus(string name) =>
            name.Equals("Deleted", StringComparison.OrdinalIgnoreCase)
                ? Dropbox.Api.Files.FileStatus.Deleted.Instance
                : Dropbox.Api.Files.FileStatus.Active.Instance;

        private static SearchOrderBy MapOrderBy(string name) =>
            name.Equals("LastModifiedTime", StringComparison.OrdinalIgnoreCase)
                ? SearchOrderBy.LastModifiedTime.Instance
                : SearchOrderBy.Relevance.Instance;
    }
}
