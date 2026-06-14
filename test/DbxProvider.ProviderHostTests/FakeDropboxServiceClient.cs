using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dropbox.Api.Files;
using IntelliTect.Dropbox;

namespace DbxProvider.ProviderHostTests;

/// <summary>
/// In-memory <see cref="DropboxServiceClient"/> used to drive the PowerShell
/// provider in-process without touching the Dropbox API. The read-path methods
/// exercised by <c>Get-ChildItem</c>/<c>Get-Item</c> are overridden, and
/// <see cref="UploadAsync"/> is overridden to record every upload (see
/// <see cref="Uploads"/>) so tests can assert how many server revisions a single
/// content cmdlet produces.
/// </summary>
public class FakeDropboxServiceClient : DropboxServiceClient
{
    private readonly List<DropboxItem> _items;
    private readonly Queue<ListFolderDelta> _scriptedDeltas = new();
    private int _fullListCalls;
    private int _continueCalls;
    private int _getLatestCursorCalls;
    private string _fullCursor = "cursor-full-0";

    /// <summary>Records every <see cref="UploadAsync"/> call in order, capturing
    /// the normalized path and the exact bytes uploaded. Tests use this to assert
    /// how many server revisions a single cmdlet produced and what they contained.</summary>
    public List<UploadRecord> Uploads { get; } = new();

    /// <summary>A single recorded upload: the normalized path, the exact payload
    /// bytes that were uploaded (captured from the stream's current position to its
    /// end, mirroring what the real client would send), and that payload's length.</summary>
    public sealed record UploadRecord(string Path, byte[] Content)
    {
        /// <summary>Number of bytes uploaded.</summary>
        public long Length => Content.Length;
    }

    public FakeDropboxServiceClient(IEnumerable<DropboxItem> items)
        : base((Dropbox.Api.DropboxClient)null!)
    {
        _items = items.ToList();
    }

    /// <summary>Number of times the full recursive listing path was invoked.</summary>
    public int FullListCalls => _fullListCalls;

    /// <summary>Number of times the /list_folder/continue delta path was invoked.</summary>
    public int ContinueCalls => _continueCalls;

    /// <summary>Number of times the get_latest_cursor path was invoked (sync-cursor capture).</summary>
    public int GetLatestCursorCalls => _getLatestCursorCalls;

    /// <summary>Scripted deltas returned, in order, by continue calls whose cursor
    /// is a sync cursor (one produced by <see cref="GetLatestCursorAsync"/>). Each
    /// dequeued delta simulates one page of account-wide changes; when the queue is
    /// empty a terminal empty delta is returned. Separate from <c>_scriptedDeltas</c>
    /// so account-sync drains and per-folder delta scripting never collide.</summary>
    public Queue<ListFolderDelta> SyncDeltas { get; } = new();

    /// <summary>Sets the cursor the next full recursive listing returns.</summary>
    public void SetFullCursor(string cursor) => _fullCursor = cursor;

    /// <summary>Queues a delta to be returned by the next continue call (FIFO).</summary>
    public void EnqueueDelta(ListFolderDelta delta) => _scriptedDeltas.Enqueue(delta);

    /// <summary>Queues a delta to be returned by the next account-sync drain (FIFO).</summary>
    public void EnqueueSyncDelta(ListFolderDelta delta) => SyncDeltas.Enqueue(delta);

    private static bool IsUnder(string itemPath, string normalizedRoot) =>
        normalizedRoot.Length == 0
        || itemPath == normalizedRoot
        || itemPath.StartsWith(normalizedRoot + "/", System.StringComparison.Ordinal);

    public override Task<(List<DropboxItem> Items, string Cursor)> ListFolderWithCursorAsync(
        string path, bool recursive = false, bool includeDeleted = false, CancellationToken cancellationToken = default)
    {
        System.Threading.Interlocked.Increment(ref _fullListCalls);
        var norm = NormalizePath(path);
        var items = _items
            .Where(i => recursive ? IsUnder(i.Path, norm) && i.Path != norm : Parent(i.Path) == norm)
            .ToList();
        return Task.FromResult((items, _fullCursor));
    }

    public override Task<ListFolderDelta> ListFolderContinueRawAsync(string cursor, CancellationToken cancellationToken = default)
    {
        System.Threading.Interlocked.Increment(ref _continueCalls);

        // Account-wide sync drain: cursors minted by GetLatestCursorAsync.
        if (cursor.StartsWith("sync::", System.StringComparison.Ordinal))
        {
            return Task.FromResult(SyncDeltas.Count > 0
                ? SyncDeltas.Dequeue()
                : new ListFolderDelta { NewCursor = cursor, HasMore = false });
        }

        if (_scriptedDeltas.Count == 0)
            return Task.FromResult(new ListFolderDelta { NewCursor = cursor, HasMore = false });
        return Task.FromResult(_scriptedDeltas.Dequeue());
    }

    /// <summary>Mints a fresh account-wide sync cursor (<c>sync::N</c>) and counts
    /// the call so tests can prove a baseline cursor was captured exactly once.</summary>
    public override Task<string> GetLatestCursorAsync(string path, bool recursive = false, CancellationToken cancellationToken = default)
    {
        System.Threading.Interlocked.Increment(ref _getLatestCursorCalls);
        return Task.FromResult($"sync::{_getLatestCursorCalls}");
    }

    /// <summary>Revision history returned by <see cref="ListRevisionsAsync"/>, keyed
    /// by normalized path. Paths not present return an empty list.</summary>
    public Dictionary<string, List<DropboxRevision>> RevisionsByPath { get; } =
        new(System.StringComparer.OrdinalIgnoreCase);

    /// <summary>Returns the entire matching subtree as a single page so
    /// <c>Build-DropboxCache</c> can be driven without the real API.</summary>
    public override Task<ListFolderPage> ListFolderFirstPageAsync(
        string path, bool recursive = false, bool includeDeleted = false,
        bool includeMediaInfo = false, bool includeHasExplicitSharedMembers = false,
        CancellationToken cancellationToken = default)
    {
        System.Threading.Interlocked.Increment(ref _fullListCalls);
        var norm = NormalizePath(path);
        var page = new ListFolderPage { Cursor = _fullCursor, HasMore = false };
        foreach (var item in _items.Where(i =>
                     recursive ? IsUnder(i.Path, norm) && i.Path != norm : Parent(i.Path) == norm))
        {
            page.Items.Add(item);
        }
        return Task.FromResult(page);
    }

    /// <summary>Returns the scripted revision history for a file, or an empty list.</summary>
    public override Task<List<DropboxRevision>> ListRevisionsAsync(
        string path, int limit = 10, CancellationToken cancellationToken = default)
    {
        var norm = NormalizePath(path);
        return Task.FromResult(
            RevisionsByPath.TryGetValue(norm, out var revisions)
                ? revisions
                : new List<DropboxRevision>());
    }

    public override Task<DropboxItem> UploadAsync(string path, System.IO.Stream content, WriteMode? mode = null, CancellationToken cancellationToken = default)
    {
        var norm = NormalizePath(path);
        using var captured = new System.IO.MemoryStream();
        content.CopyTo(captured); // copies from the stream's current position, as the real upload would
        var bytes = captured.ToArray();
        Uploads.Add(new UploadRecord(norm, bytes));

        var item = new DropboxItem
        {
            Name = norm.Contains('/') ? norm[(norm.LastIndexOf('/') + 1)..] : norm.TrimStart('/'),
            Path = norm,
            IsFolder = false,
            Id = "id:" + norm,
            Length = (ulong)bytes.Length,
        };
        _items.RemoveAll(i => i.Path == norm);
        _items.Add(item);
        return Task.FromResult(item);
    }

    private static string Parent(string normalizedPath)
    {
        int i = normalizedPath.LastIndexOf('/');
        return i <= 0 ? "" : normalizedPath.Substring(0, i);
    }

    public override Task<DropboxAccount> GetCurrentAccountAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(new DropboxAccount { AccountId = "fake-account", Email = "fake@example.com", DisplayName = "Fake" });

    public override Task<List<DropboxItem>> ListFolderAsync(string path, bool recursive = false, bool includeDeleted = false, CancellationToken cancellationToken = default)
    {
        var norm = NormalizePath(path); // "" for root, "/A" otherwise
        var children = _items
            .Where(i => Parent(i.Path) == norm)
            .ToList();
        return Task.FromResult(children);
    }

    public override Task<DropboxItem> GetMetadataAsync(string path, bool includeDeleted = false, CancellationToken cancellationToken = default)
    {
        var norm = NormalizePath(path);
        if (string.IsNullOrEmpty(norm))
            return Task.FromResult(new DropboxItem { Name = "", Path = "/", IsFolder = true, Id = "root" });
        var item = _items.FirstOrDefault(i => i.Path == norm)
            ?? throw new System.IO.FileNotFoundException("Not found: " + norm);
        return Task.FromResult(item);
    }

    public override Task<bool> ItemExistsAsync(string path, CancellationToken cancellationToken = default)
    {
        var norm = NormalizePath(path);
        return Task.FromResult(string.IsNullOrEmpty(norm) || _items.Any(i => i.Path == norm));
    }

    /// <summary>Records every single-item delete (normalized path) and removes the
    /// item from the in-memory store so tests can assert what <c>Remove-Item</c>
    /// routed to Dropbox.</summary>
    public List<string> Deletes { get; } = new();

    public override Task DeleteAsync(string path, CancellationToken cancellationToken = default)
    {
        var norm = NormalizePath(path);
        Deletes.Add(norm);
        _items.RemoveAll(i => i.Path == norm);
        return Task.CompletedTask;
    }

    /// <summary>Records every batch delete as the normalized paths it received so
    /// tests can assert that drive-qualified inputs were stripped before the API
    /// call.</summary>
    public List<string> BatchDeletes { get; } = new();

    public override Task DeleteBatchAsync(IEnumerable<string> paths, CancellationToken cancellationToken = default)
    {
        foreach (var p in paths)
        {
            var norm = NormalizePath(p);
            BatchDeletes.Add(norm);
            _items.RemoveAll(i => i.Path == norm);
        }
        return Task.CompletedTask;
    }
}
