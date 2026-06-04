using System.Text;
using DbxProvider.FunctionalTests.Infrastructure;
using Dropbox.Api;
using Xunit;

namespace DbxProvider.FunctionalTests.Services;

[Collection("Dropbox")]
public class SharedLinkTests
{
    private readonly DropboxFixture _fixture;
    public SharedLinkTests(DropboxFixture fixture) => _fixture = fixture;

    [SkippableFact]
    public async Task CreateListGetRevoke_SharedLink()
    {
        TestSkip.IfUnavailable(_fixture);
        var svc = _fixture.Service!;
        var folder = await _fixture.NewTestFolderAsync(nameof(CreateListGetRevoke_SharedLink));
        try
        {
            var path = $"{folder}/shared.txt";
            using (var ms = new MemoryStream(Encoding.UTF8.GetBytes("shared content")))
                await svc.UploadAsync(path, ms);

            IntelliTect.Dropbox.DropboxSharedLink link;
            try
            {
                link = await svc.CreateSharedLinkAsync(path);
            }
            catch (AuthException ex) { TestSkip.OnMissingScope(ex); return; }
            catch (BadInputException ex) { TestSkip.OnMissingScope(ex); return; }

            Assert.False(string.IsNullOrWhiteSpace(link.Url));

            var listed = await svc.ListSharedLinksAsync(path);
            Assert.Contains(listed, l => l.Url == link.Url);

            var meta = await svc.GetSharedLinkMetadataAsync(link.Url);
            Assert.Equal(link.Url, meta.Url);

            await svc.RevokeSharedLinkAsync(link.Url);
        }
        finally
        {
            try { await svc.DeleteAsync(folder); } catch { }
        }
    }
}
