using System.Text;
using DbxProvider.FunctionalTests.Infrastructure;
using Dropbox.Api;
using Xunit;

namespace DbxProvider.FunctionalTests.Services;

[Collection("Dropbox")]
public class LinkTests
{
    private readonly DropboxFixture _fixture;
    public LinkTests(DropboxFixture fixture) => _fixture = fixture;

    [SkippableFact]
    public async Task GetTemporaryLink_ReturnsUrl()
    {
        TestSkip.IfUnavailable(_fixture);
        var svc = _fixture.Service!;
        var folder = await _fixture.NewTestFolderAsync(nameof(GetTemporaryLink_ReturnsUrl));
        try
        {
            var path = $"{folder}/temp.txt";
            using (var ms = new MemoryStream(Encoding.UTF8.GetBytes("temp link")))
                await svc.UploadAsync(path, ms);

            var url = await svc.GetTemporaryLinkAsync(path);
            Assert.False(string.IsNullOrWhiteSpace(url));
            Assert.StartsWith("https://", url);
        }
        finally
        {
            try { await svc.DeleteAsync(folder); } catch { }
        }
    }

    [SkippableFact]
    public async Task SaveUrl_FromTinyKnownUrl()
    {
        TestSkip.IfUnavailable(_fixture);
        var svc = _fixture.Service!;
        var folder = await _fixture.NewTestFolderAsync(nameof(SaveUrl_FromTinyKnownUrl));
        try
        {
            var path = $"{folder}/saved.bin";
            try
            {
                var result = await svc.SaveUrlAsync(path, "https://httpbin.org/bytes/64");
                Assert.False(string.IsNullOrWhiteSpace(result));
            }
            catch (RetryException ex) { TestSkip.OnRetry(ex); }
            catch (ApiException<Dropbox.Api.Files.SaveUrlError> ex) { TestSkip.OnMissingScope(ex); }
        }
        finally
        {
            try { await svc.DeleteAsync(folder); } catch { }
        }
    }
}
