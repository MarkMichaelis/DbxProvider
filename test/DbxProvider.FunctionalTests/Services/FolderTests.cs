using DbxProvider.FunctionalTests.Infrastructure;
using Xunit;

namespace DbxProvider.FunctionalTests.Services;

[Collection("Dropbox")]
public class FolderTests
{
    private readonly DropboxFixture _fixture;
    public FolderTests(DropboxFixture fixture) => _fixture = fixture;

    [SkippableFact]
    public async Task CreateFolder_Delete()
    {
        TestSkip.IfUnavailable(_fixture);
        var svc = _fixture.Service!;
        var root = await _fixture.NewTestFolderAsync(nameof(CreateFolder_Delete));
        try
        {
            var sub = $"{root}/regular";

            var f = await svc.CreateFolderAsync(sub);
            Assert.True(f.IsFolder);

            await svc.DeleteAsync(sub);
            Assert.False(await svc.ItemExistsAsync(sub));
        }
        finally
        {
            try { await svc.DeleteAsync(root); } catch { }
        }
    }
}
