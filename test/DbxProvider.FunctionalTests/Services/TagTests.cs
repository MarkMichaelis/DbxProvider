using System.Text;
using DbxProvider.FunctionalTests.Infrastructure;
using Xunit;

namespace DbxProvider.FunctionalTests.Services;

[Collection("Dropbox")]
public class TagTests
{
    private readonly DropboxFixture _fixture;
    public TagTests(DropboxFixture fixture) => _fixture = fixture;

    [SkippableFact]
    public async Task Tag_AddGetRemove()
    {
        TestSkip.IfUnavailable(_fixture);
        var svc = _fixture.Service!;
        var folder = await _fixture.NewTestFolderAsync(nameof(Tag_AddGetRemove));
        try
        {
            var path = $"{folder}/tagged.txt";
            using (var ms = new MemoryStream(Encoding.UTF8.GetBytes("tag me")))
                await svc.UploadAsync(path, ms);

            var tagText = "testtag" + Guid.NewGuid().ToString("N").Substring(0, 8);
            await svc.AddTagAsync(path, tagText);

            var tags = await svc.GetTagsAsync(path);
            Assert.Contains(tags, t => string.Equals(t.TagText, tagText, StringComparison.OrdinalIgnoreCase));

            await svc.RemoveTagAsync(path, tagText);
        }
        finally
        {
            try { await svc.DeleteAsync(folder); } catch { }
        }
    }
}
