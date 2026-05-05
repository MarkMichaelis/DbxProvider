using DbxProvider.FunctionalTests.Infrastructure;
using Dropbox.Api;
using Xunit;

namespace DbxProvider.FunctionalTests.Services;

[Collection("Dropbox")]
public class FolderTests
{
    private readonly DropboxFixture _fixture;
    public FolderTests(DropboxFixture fixture) => _fixture = fixture;

    [SkippableFact]
    public async Task CreateFolder_Delete_PermanentlyDelete()
    {
        TestSkip.IfUnavailable(_fixture);
        var svc = _fixture.Service!;
        var root = await _fixture.NewTestFolderAsync(nameof(CreateFolder_Delete_PermanentlyDelete));
        try
        {
            var sub1 = $"{root}/regular";
            var sub2 = $"{root}/permanent";

            var f1 = await svc.CreateFolderAsync(sub1);
            Assert.True(f1.IsFolder);
            var f2 = await svc.CreateFolderAsync(sub2);
            Assert.True(f2.IsFolder);

            await svc.DeleteAsync(sub1);
            Assert.False(await svc.ItemExistsAsync(sub1));

            try
            {
                await svc.DeleteAsync(sub2, permanent: true);
                Assert.False(await svc.ItemExistsAsync(sub2));
            }
            catch (BadInputException ex)
            {
                TestSkip.OnMissingScope(ex);
            }
        }
        finally
        {
            try { await svc.DeleteAsync(root); } catch { }
        }
    }
}
