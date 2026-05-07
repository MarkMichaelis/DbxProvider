using System.Text;
using DbxProvider.FunctionalTests.Infrastructure;
using DbxProvider.Services;
using Xunit;

namespace DbxProvider.FunctionalTests.Services;

[Collection("Dropbox")]
public class UploadDownloadTests
{
    private readonly DropboxFixture _fixture;
    public UploadDownloadTests(DropboxFixture fixture) => _fixture = fixture;

    [SkippableFact]
    public async Task SmallFile_RoundTrip()
    {
        TestSkip.IfUnavailable(_fixture);
        var svc = _fixture.Service!;
        var folder = await _fixture.NewTestFolderAsync(nameof(SmallFile_RoundTrip));
        try
        {
            var path = $"{folder}/small.txt";
            var content = Encoding.UTF8.GetBytes("Round trip content " + Guid.NewGuid());
            using (var ms = new MemoryStream(content))
                await svc.UploadAsync(path, ms);

            var bytes = await svc.DownloadBytesAsync(path);
            Assert.Equal(content, bytes);
        }
        finally
        {
            try { await svc.DeleteAsync(folder); } catch { }
        }
    }

    [SkippableFact]
    public async Task ChunkedUpload_RoundTrip()
    {
        // Exercises the upload-session code path (UploadSessionStart /
        // AppendV2 / Finish) without uploading 150+ MB on every CI run.
        // We dial the threshold/chunk constants down to small values via
        // the internal test hooks, so a 5 MB file goes through 4
        // upload-session calls instead of a single /2/files/upload call.
        TestSkip.IfUnavailable(_fixture);
        var svc = _fixture.Service!;

        const long size = 5L * 1024 * 1024;          // 5 MB total
        const long threshold = 1L * 1024 * 1024;     // chunked path triggers above 1 MB
        const int chunkSize = 1 * 1024 * 1024;       // 1 MB chunks  -> 5 chunks (start + 3 append + finish)

        DropboxServiceClient.UploadSessionThresholdOverride = threshold;
        DropboxServiceClient.UploadSessionChunkSizeOverride = chunkSize;
        try
        {
            var folder = await _fixture.NewTestFolderAsync(nameof(ChunkedUpload_RoundTrip));
            try
            {
                var path = $"{folder}/chunked.bin";
                await using (var src = new GeneratedStream(size))
                    await svc.UploadAsync(path, src);

                var meta = await svc.GetMetadataAsync(path);
                Assert.Equal((ulong)size, meta.Size);
            }
            finally
            {
                try { await svc.DeleteAsync(folder); } catch { }
            }
        }
        finally
        {
            DropboxServiceClient.UploadSessionThresholdOverride = null;
            DropboxServiceClient.UploadSessionChunkSizeOverride = null;
        }
    }

    private sealed class GeneratedStream : Stream
    {
        private readonly long _length;
        private long _position;
        public GeneratedStream(long length) { _length = length; }
        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => _length;
        public override long Position { get => _position; set => _position = value; }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count)
        {
            long remaining = _length - _position;
            if (remaining <= 0) return 0;
            int toRead = (int)Math.Min(count, remaining);
            for (int i = 0; i < toRead; i++) buffer[offset + i] = (byte)((_position + i) & 0xFF);
            _position += toRead;
            return toRead;
        }
        public override long Seek(long offset, SeekOrigin origin)
        {
            _position = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => _position + offset,
                _ => _length + offset
            };
            return _position;
        }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
