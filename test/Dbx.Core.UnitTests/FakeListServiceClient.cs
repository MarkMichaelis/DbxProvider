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

    /// <summary>Number of first-page recursive listing calls made.</summary>
    public int FirstPageCalls { get; private set; }

    /// <summary>Number of <c>list_folder/continue</c> calls made.</summary>
    public int ContinueCalls { get; private set; }

    /// <summary>Cursors passed to each continue call, in order.</summary>
    public List<string> ContinueCursors { get; } = new();

    /// <summary>Maximum items returned per listing page. Defaults to one page.</summary>
    public int PageSize { get; set; } = int.MaxValue;

    /// <summary>When set, the continue call whose 1-based ordinal equals this
    /// value throws <see cref="OperationCanceledException"/> to simulate an
    /// interrupted build.</summary>
    public int? ThrowOnContinueCall { get; set; }

    /// <summary>Normalized folder paths whose recursive first-page listing never
    /// returns (it completes only when its cancellation token fires). Used to
    /// simulate a "wedged" subtree so tests can exercise the descend-on-wedge
    /// fallback in <see cref="MetadataCache.BuildAsync"/>.</summary>
    public HashSet<string> HangRecursiveListFor { get; } =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Revisions returned per file path (normalized or raw).</summary>
    public Dictionary<string, List<DropboxRevision>> RevisionsByPath { get; } =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>File paths passed to each <c>ListRevisionsAsync</c> call.</summary>
    public List<string> RevisionPathsRequested { get; } = new();

    /// <summary>Last value of <c>includeMediaInfo</c> seen by a first-page call.</summary>
    public bool LastIncludeMediaInfo { get; private set; }

    /// <summary>Last value of <c>includeHasExplicitSharedMembers</c> seen by a
    /// first-page call.</summary>
    public bool LastIncludeHasExplicitSharedMembers { get; private set; }

    private static string Parent(string normalizedPath)
    {
        int i = normalizedPath.LastIndexOf('/');
        return i <= 0 ? "" : normalizedPath.Substring(0, i);
    }

    private static bool IsDescendantOf(string itemPath, string root) =>
        root.Length == 0 ||
        itemPath.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase);

    private List<DropboxItem> Recursive(string norm) =>
        _items.Where(i => IsDescendantOf(i.Path, norm)).ToList();

    private static string MakeCursor(string root, int index) => $"cur::{root}::{index}";

    private static (string Root, int Index) ParseCursor(string cursor)
    {
        var parts = cursor.Split(new[] { "::" }, StringSplitOptions.None);
        return (parts[1], int.Parse(parts[2]));
    }

    public override Task<List<DropboxItem>> ListFolderAsync(string path, bool recursive = false, bool includeDeleted = false, CancellationToken cancellationToken = default)
    {
        var norm = NormalizePath(path);
        if (recursive)
        {
            RecursiveListCalls++;
            return Task.FromResult(Recursive(norm));
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

    public override Task<ListFolderPage> ListFolderFirstPageAsync(string path, bool recursive = false,
        bool includeDeleted = false, bool includeMediaInfo = false,
        bool includeHasExplicitSharedMembers = false, CancellationToken cancellationToken = default)
    {
        FirstPageCalls++;
        var norm = NormalizePath(path);
        LastIncludeMediaInfo = includeMediaInfo;
        LastIncludeHasExplicitSharedMembers = includeHasExplicitSharedMembers;

        if (HangRecursiveListFor.Contains(norm))
        {
            // Simulate a wedge: never return a page until the call is cancelled.
            var hung = new TaskCompletionSource<ListFolderPage>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            cancellationToken.Register(() => hung.TrySetException(
                new OperationCanceledException(cancellationToken)));
            return hung.Task;
        }

        var all = Recursive(norm);
        var take = Math.Min(PageSize, all.Count);
        var page = new ListFolderPage { Cursor = MakeCursor(norm, take), HasMore = take < all.Count };
        page.Items.AddRange(all.Take(take));
        return Task.FromResult(page);
    }

    public override Task<ListFolderDelta> ListFolderContinueRawAsync(string cursor, CancellationToken cancellationToken = default)
    {
        ContinueCalls++;
        ContinueCursors.Add(cursor);
        if (ThrowOnContinueCall.HasValue && ContinueCalls == ThrowOnContinueCall.Value)
            throw new OperationCanceledException();

        var (root, index) = ParseCursor(cursor);
        var all = Recursive(root);
        var take = Math.Min(PageSize, all.Count - index);
        var next = index + take;
        var delta = new ListFolderDelta { NewCursor = MakeCursor(root, next), HasMore = next < all.Count };
        delta.AddsOrUpdates.AddRange(all.Skip(index).Take(take));
        return Task.FromResult(delta);
    }

    public override Task<List<DropboxRevision>> ListRevisionsAsync(string path, int limit = 10, CancellationToken cancellationToken = default)
    {
        RevisionPathsRequested.Add(path);
        var norm = NormalizePath(path);
        if (RevisionsByPath.TryGetValue(norm, out var revs)) return Task.FromResult(revs.ToList());
        if (RevisionsByPath.TryGetValue(path, out var raw)) return Task.FromResult(raw.ToList());
        return Task.FromResult(new List<DropboxRevision>());
    }
}