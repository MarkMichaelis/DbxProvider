using System.Text;
using DbxProvider.FunctionalTests.Infrastructure;
using Xunit;

namespace DbxProvider.FunctionalTests.Services;

[Collection("Dropbox")]
public class BatchTests
{
    private readonly DropboxFixture _fixture;
    public BatchTests(DropboxFixture fixture) => _fixture = fixture;

    [SkippableFact]
    public async Task CopyBatch_MoveBatch_DeleteBatch()
    {
        TestSkip.IfUnavailable(_fixture);
        var svc = _fixture.Service!;
        var folder = await _fixture.NewTestFolderAsync(nameof(CopyBatch_MoveBatch_DeleteBatch));
        try
        {
            var a = $"{folder}/a.txt";
            var b = $"{folder}/b.txt";
            using (var ms = new MemoryStream(Encoding.UTF8.GetBytes("a")))
                await svc.UploadAsync(a, ms);
            using (var ms = new MemoryStream(Encoding.UTF8.GetBytes("b")))
                await svc.UploadAsync(b, ms);

            var aCopy = $"{folder}/a-copy.txt";
            var bCopy = $"{folder}/b-copy.txt";
            await svc.CopyBatchAsync(new[] { (a, aCopy), (b, bCopy) });
            Assert.True(await svc.ItemExistsAsync(aCopy));
            Assert.True(await svc.ItemExistsAsync(bCopy));

            var aMoved = $"{folder}/a-moved.txt";
            var bMoved = $"{folder}/b-moved.txt";
            await svc.MoveBatchAsync(new[] { (aCopy, aMoved), (bCopy, bMoved) });
            Assert.True(await svc.ItemExistsAsync(aMoved));
            Assert.True(await svc.ItemExistsAsync(bMoved));
            Assert.False(await svc.ItemExistsAsync(aCopy));

            await svc.DeleteBatchAsync(new[] { aMoved, bMoved, a, b });
            Assert.False(await svc.ItemExistsAsync(aMoved));
            Assert.False(await svc.ItemExistsAsync(b));
        }
        finally
        {
            try { await svc.DeleteAsync(folder); } catch { }
        }
    }
}
