using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using IntelliTect.Dropbox;

namespace Dbx.Core.UnitTests;

/// <summary>
/// In-memory <see cref="DropboxServiceClient"/> backing a fixed item tree.
/// Overrides only the read-path listing methods exercised by the metadata
/// cache and records call counts so tests can assert that
/// <c>Build-DropboxCache</c> issues a single recursive listing.
/// </summary>
internal sealed class FakeListServiceClient : DropboxServiceClient
{
    private readonly List<DropboxItem> _items;

    public FakeListServiceClient(IEnumerable<DropboxItem> items)
        : base((Dropbox.Api.DropboxClient)null!)
    {
        _items = items.ToList();
    }

    /// <summary>Number of recursive <c>list_folder</c> calls made.</summary>
    public int RecursiveListCalls { get; private set; }

    /// <summary>Number of non-recursive <c>list_folder</c> calls made.</summary>
    public int NonRecursiveListCalls { get; private set; }

    private static string Parent(string normalizedPath)
    {
        int i = normalizedPath.LastIndexOf('/');
        return i <= 0 ? "" : normalizedPath.Substring(0, i);
    }

    private static bool IsDescendantOf(string itemPath, string root) =>
        root.Length == 0 ||
        itemPath.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase);

    public override Task<List<DropboxItem>> ListFolderAsync(string path, bool recursive = false, bool includeDeleted = false, CancellationToken cancellationToken = default)
    {
        var norm = NormalizePath(path);
        if (recursive)
        {
            RecursiveListCalls++;
            return Task.FromResult(_items.Where(i => IsDescendantOf(i.Path, norm)).ToList());
        }

        NonRecursiveListCalls++;
        return Task.FromResult(_items.Where(i => Parent(i.Path) == norm).ToList());
    }

    public override Task<(List<DropboxItem> Items, string Cursor)> ListFolderWithCursorAsync(string path, bool recursive = false, bool includeDeleted = false, CancellationToken cancellationToken = default)
    {
        var norm = NormalizePath(path);
        NonRecursiveListCalls++;
        var children = _items.Where(i => Parent(i.Path) == norm).ToList();
        return Task.FromResult((children, $"cursor::{norm}"));
    }
}