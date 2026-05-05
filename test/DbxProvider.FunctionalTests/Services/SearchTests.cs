using System.Text;
using DbxProvider.FunctionalTests.Infrastructure;
using Xunit;

namespace DbxProvider.FunctionalTests.Services;

[Collection("Dropbox")]
public class SearchTests
{
    private readonly DropboxFixture _fixture;
    public SearchTests(DropboxFixture fixture) => _fixture = fixture;

    [SkippableFact]
    public async Task Search_FindsFileByUniqueToken()
    {
        TestSkip.IfUnavailable(_fixture);
        var svc = _fixture.Service!;
        var folder = await _fixture.NewTestFolderAsync(nameof(Search_FindsFileByUniqueToken));
        try
        {
            var token = "uniq" + Guid.NewGuid().ToString("N").Substring(0, 12);
            var path = $"{folder}/{token}.txt";
            using (var ms = new MemoryStream(Encoding.UTF8.GetBytes("search target")))
                await svc.UploadAsync(path, ms);

            List<DbxProvider.Models.DropboxSearchResult> results = new();
            for (int i = 0; i < 12; i++)
            {
                results = await svc.SearchAsync(token, folder, maxResults: 25);
                if (results.Count > 0) break;
                await Task.Delay(2500);
            }

            Assert.NotEmpty(results);
            Assert.Contains(results, r => r.Item != null && r.Item.Name.Contains(token, StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            try { await svc.DeleteAsync(folder); } catch { }
        }
    }
}
