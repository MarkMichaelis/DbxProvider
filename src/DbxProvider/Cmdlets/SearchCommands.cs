using System;
using System.Linq;
using System.Management.Automation;
using DbxProvider.Models;
using DbxProvider.Services;
using Dropbox.Api.Files;

namespace DbxProvider.Cmdlets
{
    /// <summary>
    /// Searches for files and folders in Dropbox using the indexed search_v2 API.
    /// Note: Dropbox search is prefix-token-based, not glob — '*' and '?' in
    /// Query are treated as literals. Use -Wildcard for PowerShell wildcard
    /// semantics, or -FileExtensions for server-side extension filtering.
    /// </summary>
    [Cmdlet(VerbsCommon.Search, "Dropbox")]
    [OutputType(typeof(DropboxSearchResult))]
    public class SearchDropboxCommand : DropboxCmdletBase
    {
        [Parameter(Mandatory = true, Position = 0)]
        public string Query { get; set; } = string.Empty;

        [Parameter]
        public string Path { get; set; } = "";

        [Parameter]
        public int MaxResults { get; set; } = 100;

        [Parameter]
        public SwitchParameter IncludeHighlights { get; set; }

        /// <summary>Restrict matching to filenames (skip file content indexing).</summary>
        [Parameter]
        public SwitchParameter FilenameOnly { get; set; }

        /// <summary>Server-side filter on file extensions (e.g. pdf, docx).</summary>
        [Parameter]
        public string[]? FileExtensions { get; set; }

        /// <summary>Server-side filter on Dropbox file categories.</summary>
        [Parameter]
        [ValidateSet("Image", "Document", "Pdf", "Spreadsheet", "Presentation",
            "Audio", "Video", "Folder", "Paper", "Others",
            IgnoreCase = true)]
        public string[]? FileCategory { get; set; }

        /// <summary>Search active or deleted files (default: Active).</summary>
        [Parameter]
        [ValidateSet("Active", "Deleted", IgnoreCase = true)]
        public string FileStatus { get; set; } = "Active";

        /// <summary>Result ordering (default: Relevance).</summary>
        [Parameter]
        [ValidateSet("Relevance", "LastModifiedTime", IgnoreCase = true)]
        public string OrderBy { get; set; } = "Relevance";

        /// <summary>
        /// Treat Query as a PowerShell wildcard (*, ?, [abc]). Implies
        /// -FilenameOnly. Tokens are derived from the pattern for the server
        /// query, and results are post-filtered with WildcardPattern to
        /// enforce true wildcard semantics.
        /// </summary>
        [Parameter]
        public SwitchParameter Wildcard { get; set; }

        protected override void ProcessRecord()
        {
            try
            {
                var service = GetService();

                if (Wildcard)
                {
                    var items = service.SearchByFilenameAsync(Query, Path, MaxResults)
                        .GetAwaiter().GetResult();
                    foreach (var item in items)
                    {
                        WriteObject(new DropboxSearchResult
                        {
                            MatchType = "Filename",
                            Item = item,
                        });
                    }
                    WriteVerbose($"Found {items.Count} results for wildcard '{Query}'");
                    return;
                }

                var categories = FileCategory?
                    .Select(MapCategory)
                    .ToArray();

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
                {
                    WriteObject(result);
                }

                WriteVerbose($"Found {results.Count} results for '{Query}'");
            }
            catch (Exception ex)
            {
                WriteError(new ErrorRecord(ex, "SearchFailed",
                    ErrorCategory.ReadError, Query));
            }
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