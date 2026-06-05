using System.Collections;
using System.Collections.Generic;
using DbxProvider.Provider;
using IntelliTect.Dropbox;
using Xunit;

namespace DbxProvider.ProviderHostTests;

/// <summary>
/// Unit tests for <see cref="DropboxContentWriter"/> proving it does not upload a
/// spurious zero-byte revision when a writer is opened and closed without any
/// content being written, while still uploading when an (even empty) value is
/// written (so <c>Set-Content -Value '''' </c> truncates to empty).
/// </summary>
public class DropboxContentWriterTests
{
    private static FakeDropboxServiceClient NewFake() =>
        new(new List<DropboxItem>());

    [Fact]
    public void Close_WithoutAnyWrite_DoesNotUpload()
    {
        var fake = NewFake();
        var writer = new DropboxContentWriter(fake, "/A/b.txt");

        writer.Close();

        Assert.Empty(fake.Uploads);
    }

    [Fact]
    public void Close_AfterWritingEmptyString_Uploads()
    {
        var fake = NewFake();
        var writer = new DropboxContentWriter(fake, "/A/b.txt");

        writer.Write(new ArrayList { "" });
        writer.Close();

        Assert.Single(fake.Uploads);
        Assert.Equal("/A/b.txt", fake.Uploads[0].Path);
    }
}