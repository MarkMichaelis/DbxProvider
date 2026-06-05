using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dropbox.Api.Files;
using IntelliTect.Dropbox;

namespace DbxProvider.ProviderHostTests;

/// <summary>
/// In-memory <see cref="DropboxServiceClient"/> used to drive the PowerShell
/// provider in-process without touching the Dropbox API. Only the read-path
/// methods exercised by <c>Get-ChildItem</c>/<c>Get-Item</c> are overridden.
/// </summary>
public class FakeDropboxServiceClient : DropboxServiceClient
{
    private readonly List<DropboxItem> _items;
    private readonly Queue<ListFolderDelta> _scriptedDeltas = new();
    private int _fullListCalls;
    private int _continueCalls;
    private string _fullCursor = "cursor-full-0";

    /// <summary>Records every <see cref="UploadAsync"/> call in order, capturing
    /// the normalized path and the byte length of the uploaded stream. Tests use
    /// this to assert how many server revisions a single cmdlet produced.</summary>
    public List<UploadRecord> Uploads { get; } = new();

    /// <summary>A single recorded upload: the normalized path and the number of
    /// bytes written to the server.</summary>
    public sealed record UploadRecord(string Path, long Length);

    public FakeDropboxServiceClient(IEnumerable<DropboxItem> items)
        : base((Dropbox.Api.DropboxClient)null!)
    {
        _items = items.ToList();
    }

    /// <summary>Number of times the full recursive listing path was invoked.</summary>
    public int FullListCalls => _fullListCalls;

    /// <summary>Number of times the /list_folder/continue delta path was invoked.</summary>
    public int ContinueCalls => _continueCalls;

    /// <summary>Sets the cursor the next full recursive listing returns.</summary>
    public void SetFullCursor(string cursor) => _fullCursor = cursor;

    /// <summary>Queues a delta to be returned by the next continue call (FIFO).</summary>
    public void EnqueueDelta(ListFolderDelta delta) => _scriptedDeltas.Enqueue(delta);

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
        if (_scriptedDeltas.Count == 0)
            return Task.FromResult(new ListFolderDelta { NewCursor = cursor, HasMore = false });
        return Task.FromResult(_scriptedDeltas.Dequeue());
    }

    public override Task<DropboxItem> UploadAsync(string path, System.IO.Stream content, WriteMode? mode = null, CancellationToken cancellationToken = default)
    {
        var norm = NormalizePath(path);
        long length = content.CanSeek ? content.Length : -1;
        Uploads.Add(new UploadRecord(norm, length));

        var item = new DropboxItem
        {
            Name = norm.Contains('/') ? norm[(norm.LastIndexOf('/') + 1)..] : norm.TrimStart('/'),
            Path = norm,
            IsFolder = false,
            Id = "id:" + norm,
            Length = length < 0 ? 0 : (ulong)length,
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
}
