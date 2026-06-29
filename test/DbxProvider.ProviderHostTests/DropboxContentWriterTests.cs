using System.Collections.Generic;
using System.IO;
using System.Text;
using DbxProvider.Provider;
using IntelliTect.Dropbox;
using Xunit;

namespace DbxProvider.ProviderHostTests;

/// <summary>
/// Direct behavior tests for <see cref="DropboxContentWriter"/> covering the
/// append-safety guards: a writer opened for append that never receives content
/// (only a seek-to-end) must not upload an empty buffer, which would truncate the
/// existing file. A normal overwrite (non-append) still uploads.
/// </summary>
public class DropboxContentWriterTests
{
    private static FakeDropboxServiceClient FakeWithFile(string path, string content)
    {
        var fake = new FakeDropboxServiceClient(new List<DropboxItem>
        {
            new() { Name = "b", Path = path, IsFolder = false, Id = "id:" + path },
        });
        fake.FileBytes[path] = Encoding.UTF8.GetBytes(content);
        return fake;
    }

    [Fact]
    public void Close_AppendMode_WithNoWrite_DoesNotUpload_SoFileIsNotTruncated()
    {
        var fake = FakeWithFile("/A/b.txt", "keep me");
        var writer = new DropboxContentWriter(fake, "/A/b.txt", raw: false);

        // Append intent (seek to end) but no content written before close.
        writer.Seek(0, SeekOrigin.End);
        writer.Close();

        Assert.Empty(fake.Uploads); // must NOT overwrite the existing file with zero bytes
    }

    [Fact]
    public void Close_NonAppendMode_WithNoWrite_StillUploads_PreservingOverwriteSemantics()
    {
        var fake = FakeWithFile("/A/b.txt", "old");
        var writer = new DropboxContentWriter(fake, "/A/b.txt", raw: false);

        // No append intent: this is the Set-Content/overwrite path, which intentionally
        // replaces the file (here with zero bytes) and must still upload.
        writer.Close();

        Assert.Single(fake.Uploads);
        Assert.Equal(0, fake.Uploads[0].Length);
    }

    [Fact]
    public void LargeContent_SpoolsToTempFileOnDisk_NotRam_ThenUploadsRoundTrip()
    {
        var fake = new FakeDropboxServiceClient(new List<DropboxItem>());
        var spoolDir = Path.Combine(Path.GetTempPath(), "DbxSpoolTests-" + System.Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(spoolDir);
        try
        {
            var writer = new DropboxContentWriter(fake, "/A/big.bin", raw: true, spoolDirectory: spoolDir);
            var payload = new byte[2 * 1024 * 1024];
            for (int i = 0; i < payload.Length; i++) payload[i] = (byte)(i % 251);

            writer.Write(new List<object> { payload });

            // The upload is spooled to disk, so a temp file exists before close.
            Assert.NotEmpty(Directory.GetFiles(spoolDir));

            writer.Close();

            // The temp file is cleaned up after close, and the upload round-trips.
            Assert.Empty(Directory.GetFiles(spoolDir));
            var upload = Assert.Single(fake.Uploads);
            Assert.Equal(payload, upload.Content);
        }
        finally
        {
            try { Directory.Delete(spoolDir, recursive: true); } catch { /* best effort */ }
        }
    }
}
