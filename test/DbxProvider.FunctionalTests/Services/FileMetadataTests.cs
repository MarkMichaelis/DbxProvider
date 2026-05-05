using System.Text;
using DbxProvider.FunctionalTests.Infrastructure;
using Xunit;

namespace DbxProvider.FunctionalTests.Services;

[Collection("Dropbox")]
public class FileMetadataTests
{
    private readonly DropboxFixture _fixture;
    public FileMetadataTests(DropboxFixture fixture) => _fixture = fixture;

    [SkippableFact]
    public async Task ListFolder_GetMetadata_ItemExists_WorkOnRealApi()
    {
        TestSkip.IfUnavailable(_fixture);
        var svc = _fixture.Service!;
        var folder = await _fixture.NewTestFolderAsync(nameof(ListFolder_GetMetadata_ItemExists_WorkOnRealApi));
        try
        {
            var filePath = $"{folder}/hello.txt";
            using (var ms = new MemoryStream(Encoding.UTF8.GetBytes("hello world")))
                await svc.UploadAsync(filePath, ms);

            var entries = await svc.ListFolderAsync(folder);
            Assert.Contains(entries, e => e.Name == "hello.txt");

            var meta = await svc.GetMetadataAsync(filePath);
            Assert.Equal("hello.txt", meta.Name);
            Assert.False(meta.IsFolder);

            Assert.True(await svc.ItemExistsAsync(filePath));
            Assert.False(await svc.ItemExistsAsync($"{folder}/does-not-exist-{Guid.NewGuid():N}.txt"));
        }
        finally
        {
            try { await svc.DeleteAsync(folder); } catch { }
        }
    }
}
