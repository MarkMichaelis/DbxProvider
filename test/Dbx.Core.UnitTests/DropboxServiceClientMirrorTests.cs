using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using IntelliTect.Dropbox;
using FluentAssertions;
using Xunit;

namespace Dbx.Core.UnitTests;

/// <summary>
/// Verifies that <see cref="DropboxServiceClient"/> serves a download from a
/// configured, verified local mirror without performing an API transfer. The
/// test client has no live Dropbox connection, so a returned payload can only
/// have come from the local mirror short-circuit; reverting it would force the
/// API path and fail.
/// </summary>
public class DropboxServiceClientMirrorTests
{
    private sealed class MetadataStubClient : DropboxServiceClient
    {
        private readonly DropboxItem _master;

        public MetadataStubClient(DropboxItem master) : base("fake-token") => _master = master;

        public int GetMetadataCallCount { get; private set; }

        public override Task<DropboxItem> GetMetadataAsync(
            string path, bool includeDeleted = false, CancellationToken cancellationToken = default)
        {
            GetMetadataCallCount++;
            return Task.FromResult(_master);
        }
    }

    [Fact]
    public async Task DownloadBytesAsync_VerifiedMirrorHit_ReturnsLocalBytesWithoutApiCall()
    {
        string root = Path.Combine(Path.GetTempPath(), "dbx-svc-mirror-" + Path.GetRandomFileName());
        Directory.CreateDirectory(root);
        byte[] bytes = Encoding.ASCII.GetBytes("served from the local mirror");
        File.WriteAllBytes(Path.Combine(root, "f.txt"), bytes);
        var master = new DropboxItem
        {
            Path = "/f.txt",
            Length = (ulong)bytes.Length,
            ContentHash = DropboxContentHasher.ComputeHash(new MemoryStream(bytes)),
        };
        var client = new MetadataStubClient(master)
        {
            Mirror = new LocalMirrorResolver(new LocalMirrorOptions { Root = root }),
        };

        byte[] result = await client.DownloadBytesAsync("/f.txt");

        result.Should().Equal(bytes);
    }

    [Fact]
    public async Task DownloadBytesAsync_CachedMetadataProvidesHash_ServesLocalWithoutMetadataApiCall()
    {
        string root = Path.Combine(Path.GetTempPath(), "dbx-svc-mirror-" + Path.GetRandomFileName());
        Directory.CreateDirectory(root);
        byte[] bytes = Encoding.ASCII.GetBytes("served via cached content hash");
        File.WriteAllBytes(Path.Combine(root, "f.txt"), bytes);
        var master = new DropboxItem
        {
            Path = "/f.txt",
            Length = (ulong)bytes.Length,
            ContentHash = DropboxContentHasher.ComputeHash(new MemoryStream(bytes)),
        };
        var client = new MetadataStubClient(master)
        {
            Mirror = new LocalMirrorResolver(new LocalMirrorOptions { Root = root }),
            CachedMetadataProvider = _ => master,
        };

        byte[] result = await client.DownloadBytesAsync("/f.txt");

        result.Should().Equal(bytes);
        client.GetMetadataCallCount.Should().Be(0);
    }

    [Fact]
    public async Task DownloadBytesAsync_CachedMetadataLacksHash_FallsBackToMetadataApi()
    {
        string root = Path.Combine(Path.GetTempPath(), "dbx-svc-mirror-" + Path.GetRandomFileName());
        Directory.CreateDirectory(root);
        byte[] bytes = Encoding.ASCII.GetBytes("hash missing in cache");
        File.WriteAllBytes(Path.Combine(root, "f.txt"), bytes);
        var master = new DropboxItem
        {
            Path = "/f.txt",
            Length = (ulong)bytes.Length,
            ContentHash = DropboxContentHasher.ComputeHash(new MemoryStream(bytes)),
        };
        var client = new MetadataStubClient(master)
        {
            Mirror = new LocalMirrorResolver(new LocalMirrorOptions { Root = root }),
            CachedMetadataProvider = _ => new DropboxItem { Path = "/f.txt", ContentHash = "" },
        };

        byte[] result = await client.DownloadBytesAsync("/f.txt");

        result.Should().Equal(bytes);
        client.GetMetadataCallCount.Should().Be(1);
    }
}
