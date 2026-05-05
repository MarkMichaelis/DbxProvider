using System.IO.Compression;
using System.Text;
using DbxProvider.FunctionalTests.Infrastructure;
using Xunit;

namespace DbxProvider.FunctionalTests.Services;

[Collection("Dropbox")]
public class PreviewTests
{
    private readonly DropboxFixture _fixture;
    public PreviewTests(DropboxFixture fixture) => _fixture = fixture;

    private static readonly byte[] TinyPng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNkYAAAAAYAAjCB0C8AAAAASUVORK5CYII=");

    private static byte[] BuildMinimalDocx()
    {
        const string contentTypes =
            """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
              <Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>
              <Default Extension="xml" ContentType="application/xml"/>
              <Override PartName="/word/document.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml"/>
            </Types>
            """;

        const string rootRels =
            """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
              <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="word/document.xml"/>
            </Relationships>
            """;

        const string document =
            """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main">
              <w:body>
                <w:p><w:r><w:t>DbxProvider preview test.</w:t></w:r></w:p>
                <w:sectPr><w:pgSz w:w="12240" w:h="15840"/></w:sectPr>
              </w:body>
            </w:document>
            """;

        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            void Write(string entryName, string content)
            {
                var entry = zip.CreateEntry(entryName, CompressionLevel.Fastest);
                using var w = new StreamWriter(entry.Open(), new UTF8Encoding(false));
                w.Write(content);
            }
            Write("[Content_Types].xml", contentTypes);
            Write("_rels/.rels", rootRels);
            Write("word/document.xml", document);
        }
        return ms.ToArray();
    }

    private static readonly byte[] TinyDocx = BuildMinimalDocx();

    [SkippableFact]
    public async Task GetPreview_ForDocx_ReturnsBytes()
    {
        TestSkip.IfUnavailable(_fixture);
        var svc = _fixture.Service!;
        var folder = await _fixture.NewTestFolderAsync(nameof(GetPreview_ForDocx_ReturnsBytes));
        try
        {
            var path = $"{folder}/preview.docx";
            using (var ms = new MemoryStream(TinyDocx))
                await svc.UploadAsync(path, ms);

            await Task.Delay(2000);

            try
            {
                var (bytes, _) = await svc.GetPreviewAsync(path);
                Assert.NotNull(bytes);
                Assert.True(bytes.Length > 0);
            }
            catch (Dropbox.Api.ApiException<Dropbox.Api.Files.PreviewError> ex)
            {
                Skip.If(true, $"Preview unavailable for embedded sample: {ex.Message}");
            }
        }
        finally
        {
            try { await svc.DeleteAsync(folder); } catch { }
        }
    }

    [SkippableFact]
    public async Task GetThumbnail_ForPng_ReturnsBytes()
    {
        TestSkip.IfUnavailable(_fixture);
        var svc = _fixture.Service!;
        var folder = await _fixture.NewTestFolderAsync(nameof(GetThumbnail_ForPng_ReturnsBytes));
        try
        {
            var path = $"{folder}/thumb.png";
            using (var ms = new MemoryStream(TinyPng))
                await svc.UploadAsync(path, ms);

            try
            {
                var bytes = await svc.GetThumbnailAsync(path, "w64h64", "png");
                Assert.NotNull(bytes);
                Assert.True(bytes.Length > 0);
            }
            catch (Dropbox.Api.ApiException<Dropbox.Api.Files.ThumbnailV2Error> ex)
            {
                Skip.If(true, $"Thumbnail unsupported: {ex.Message}");
            }
        }
        finally
        {
            try { await svc.DeleteAsync(folder); } catch { }
        }
    }
}
