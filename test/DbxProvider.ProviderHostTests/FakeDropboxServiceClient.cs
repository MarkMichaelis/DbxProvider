using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using IntelliTect.Dropbox;

namespace DbxProvider.ProviderHostTests;

/// <summary>
/// In-memory <see cref="DropboxServiceClient"/> used to drive the PowerShell
/// provider in-process without touching the Dropbox API. Only the read-path
/// methods exercised by <c>Get-ChildItem</c>/<c>Get-Item</c> are overridden.
/// </summary>
public sealed class FakeDropboxServiceClient : DropboxServiceClient
{
    private readonly List<DropboxItem> _items;

    public FakeDropboxServiceClient(IEnumerable<DropboxItem> items)
        : base((Dropbox.Api.DropboxClient)null!)
    {
        _items = items.ToList();
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
