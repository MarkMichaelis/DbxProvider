using DbxProvider.FunctionalTests.Infrastructure;
using Dropbox.Api;
using Xunit;

namespace DbxProvider.FunctionalTests.Services;

[Collection("Dropbox")]
public class SharedFolderTests
{
    private readonly DropboxFixture _fixture;
    public SharedFolderTests(DropboxFixture fixture) => _fixture = fixture;

    [SkippableFact]
    public async Task ShareListUnshare_Folder()
    {
        TestSkip.IfUnavailable(_fixture);
        var svc = _fixture.Service!;
        var folder = await _fixture.NewTestFolderAsync(nameof(ShareListUnshare_Folder));
        string? sharedId = null;
        try
        {
            try
            {
                sharedId = await svc.ShareFolderAsync(folder);
            }
            catch (AuthException ex) { TestSkip.OnMissingScope(ex); return; }
            catch (BadInputException ex) { TestSkip.OnMissingScope(ex); return; }

            Assert.False(string.IsNullOrWhiteSpace(sharedId));

            var listed = await svc.ListSharedFoldersAsync();
            Assert.Contains(listed, f => f.SharedFolderId == sharedId);
        }
        finally
        {
            if (!string.IsNullOrEmpty(sharedId))
            {
                try { await svc.UnshareFolderAsync(sharedId); } catch { }
            }
            try { await svc.DeleteAsync(folder); } catch { }
        }
    }
}
