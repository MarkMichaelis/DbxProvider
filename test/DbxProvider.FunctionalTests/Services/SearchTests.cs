using System.Text;
using DbxProvider.FunctionalTests.Infrastructure;
using Dropbox.Api.Files;
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

    [SkippableFact]
    public async Task Search_FilenameOnly_AndExtensionFilter()
    {
        TestSkip.IfUnavailable(_fixture);
        var svc = _fixture.Service!;
        var folder = await _fixture.NewTestFolderAsync(nameof(Search_FilenameOnly_AndExtensionFilter));
        try
        {
            var token = "uniq" + Guid.NewGuid().ToString("N").Substring(0, 12);
            var txtPath = $"{folder}/{token}.txt";
            var logPath = $"{folder}/{token}.log";
            using (var ms = new MemoryStream(Encoding.UTF8.GetBytes("a")))
                await svc.UploadAsync(txtPath, ms);
            using (var ms = new MemoryStream(Encoding.UTF8.GetBytes("b")))
                await svc.UploadAsync(logPath, ms);

            List<DbxProvider.Models.DropboxSearchResult> results = new();
            for (int i = 0; i < 12; i++)
            {
                results = await svc.SearchAsync(token, folder, maxResults: 25,
                    filenameOnly: true, fileExtensions: new[] { "txt" });
                if (results.Count > 0) break;
                await Task.Delay(2500);
            }

            Assert.NotEmpty(results);
            Assert.All(results, r => Assert.EndsWith(".txt", r.Item!.Name, StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            try { await svc.DeleteAsync(folder); } catch { }
        }
    }

    [SkippableFact]
    public async Task SearchByFilename_WildcardPattern_PostFilters()
    {
        TestSkip.IfUnavailable(_fixture);
        var svc = _fixture.Service!;
        var folder = await _fixture.NewTestFolderAsync(nameof(SearchByFilename_WildcardPattern_PostFilters));
        try
        {
            var token = "wild" + Guid.NewGuid().ToString("N").Substring(0, 10);
            var txtPath = $"{folder}/{token}.txt";
            var logPath = $"{folder}/{token}.log";
            using (var ms = new MemoryStream(Encoding.UTF8.GetBytes("a")))
                await svc.UploadAsync(txtPath, ms);
            using (var ms = new MemoryStream(Encoding.UTF8.GetBytes("b")))
                await svc.UploadAsync(logPath, ms);

            List<DbxProvider.Models.DropboxItem> items = new();
            for (int i = 0; i < 12; i++)
            {
                items = await svc.SearchByFilenameAsync($"{token}*.txt", folder, maxResults: 25);
                if (items.Count > 0) break;
                await Task.Delay(2500);
            }

            Assert.NotEmpty(items);
            Assert.All(items, i => Assert.EndsWith(".txt", i.Name, StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            try { await svc.DeleteAsync(folder); } catch { }
        }
    }

    [SkippableFact]
    public async Task Search_OrderBy_LastModifiedTime_DoesNotThrow()
    {
        TestSkip.IfUnavailable(_fixture);
        var svc = _fixture.Service!;
        var folder = await _fixture.NewTestFolderAsync(nameof(Search_OrderBy_LastModifiedTime_DoesNotThrow));
        try
        {
            var token = "ord" + Guid.NewGuid().ToString("N").Substring(0, 10);
            using (var ms = new MemoryStream(Encoding.UTF8.GetBytes("c")))
                await svc.UploadAsync($"{folder}/{token}.txt", ms);

            // Just exercise the parameter — index latency may yield 0 results.
            var results = await svc.SearchAsync(token, folder, maxResults: 5,
                orderBy: SearchOrderBy.LastModifiedTime.Instance);
            Assert.NotNull(results);
        }
        finally
        {
            try { await svc.DeleteAsync(folder); } catch { }
        }
    }
}
