using System.Text;
using DbxProvider.FunctionalTests.Infrastructure;
using Xunit;

namespace DbxProvider.FunctionalTests.Services;

[Collection("Dropbox")]
public class CopyMoveTests
{
    private readonly DropboxFixture _fixture;
    public CopyMoveTests(DropboxFixture fixture) => _fixture = fixture;

    [SkippableFact]
    public async Task Copy_SingleFile()
    {
        TestSkip.IfUnavailable(_fixture);
        var svc = _fixture.Service!;
        var folder = await _fixture.NewTestFolderAsync(nameof(Copy_SingleFile));
        try
        {
            var src = $"{folder}/src.txt";
            var dst = $"{folder}/dst.txt";
            using (var ms = new MemoryStream(Encoding.UTF8.GetBytes("copy me")))
                await svc.UploadAsync(src, ms);

            var copied = await svc.CopyAsync(src, dst);
            Assert.Equal("dst.txt", copied.Name);
            Assert.True(await svc.ItemExistsAsync(src));
            Assert.True(await svc.ItemExistsAsync(dst));
        }
        finally
        {
            try { await svc.DeleteAsync(folder); } catch { }
        }
    }

    [SkippableFact]
    public async Task Move_SingleFile_Rename()
    {
        TestSkip.IfUnavailable(_fixture);
        var svc = _fixture.Service!;
        var folder = await _fixture.NewTestFolderAsync(nameof(Move_SingleFile_Rename));
        try
        {
            var src = $"{folder}/before.txt";
            var dst = $"{folder}/after.txt";
            using (var ms = new MemoryStream(Encoding.UTF8.GetBytes("move me")))
                await svc.UploadAsync(src, ms);

            var moved = await svc.MoveAsync(src, dst);
            Assert.Equal("after.txt", moved.Name);
            Assert.False(await svc.ItemExistsAsync(src));
            Assert.True(await svc.ItemExistsAsync(dst));
        }
        finally
        {
            try { await svc.DeleteAsync(folder); } catch { }
        }
    }
}
