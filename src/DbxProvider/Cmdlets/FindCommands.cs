using System;
using System.Management.Automation;
using IntelliTect.Dropbox;

namespace DbxProvider.Cmdlets
{
    /// <summary>
    /// Finds files and folders by filename wildcard straight from the local
    /// metadata cache, issuing zero Dropbox API calls for the enumeration itself.
    /// The cache is auto-refreshed from the account delta cursor first (the shared
    /// cross-cutting refresh), so results reflect changes since the last sync.
    /// Use this for fast, repeatable name searches over a fully-built cache; for
    /// the server-side indexed search use Search-Dropbox instead. Build or refresh
    /// the cache with Build-DropboxCacheAll.ps1.
    /// </summary>
    [Cmdlet(VerbsCommon.Find, "DropboxItem")]
    [OutputType(typeof(DropboxItem))]
    public class FindDropboxItemCommand : DropboxCmdletBase
    {
        /// <summary>Filename wildcard (PowerShell -like semantics) matched against
        /// each item's name. Defaults to '*' (every item).</summary>
        [Parameter(Position = 0)]
        [SupportsWildcards()]
        public string Name { get; set; } = "*";

        /// <summary>Dropbox path (or drive path such as <c>Dbx:\Folder</c>) to
        /// search under. Defaults to the account root.</summary>
        [Parameter]
        public string Path { get; set; } = string.Empty;

        /// <summary>Match only zero-byte files (skips folders and non-empty files).</summary>
        [Parameter]
        public SwitchParameter ZeroByteOnly { get; set; }

        /// <summary>Refreshes the cache, then emits each cached item whose name
        /// matches -Name under -Path.</summary>
        protected override void ProcessRecord()
        {
            var cache = GetRefreshedCache();
            var startPath = StripDrivePrefix(Path);

            if (cache.PersistedCount() == 0)
            {
                WriteWarning(
                    "The metadata cache is empty. Run Build-DropboxCacheAll.ps1 (or " +
                    "Build-DropboxCache) to populate it before searching.");
                return;
            }

            // Stream straight from the cache enumerator and emit as we go, so a
            // broad pattern (such as the default '*') never materializes the whole
            // result set in memory. EnumerateItems yields each item once, so no
            // de-duplication pass is needed here.
            var predicate = BuildNamePredicate(Name, ZeroByteOnly.IsPresent);
            foreach (var item in cache.EnumerateItems(startPath))
                if (predicate(item))
                    WriteObject(item);
        }

        /// <summary>
        /// Builds the shared name/zero-byte predicate used by both Find-DropboxItem
        /// and Find-DropboxConflict: a wildcard match on the item name and,
        /// when <paramref name="zeroByteOnly"/> is set, a restriction to zero-byte
        /// files (folders and non-empty files are excluded).
        /// </summary>
        internal static Func<DropboxItem, bool> BuildNamePredicate(string namePattern, bool zeroByteOnly)
        {
            // A blank or whitespace pattern (e.g. a computed-but-empty -Name)
            // means "no filter", so treat it the same as the default '*' rather
            // than an empty literal that would match only empty filenames.
            var pattern = string.IsNullOrWhiteSpace(namePattern) ? "*" : namePattern;
            var matcher = new WildcardMatcher(pattern);
            return item =>
                matcher.IsMatch(item.Name)
                && (!zeroByteOnly || (!item.IsFolder && item.Length == 0));
        }
    }
}