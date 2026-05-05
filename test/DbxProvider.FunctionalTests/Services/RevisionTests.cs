using System.Text;
using DbxProvider.FunctionalTests.Infrastructure;
using Xunit;

namespace DbxProvider.FunctionalTests.Services;

[Collection("Dropbox")]
public class RevisionTests
{
    private readonly DropboxFixture _fixture;
    public RevisionTests(DropboxFixture fixture) => _fixture = fixture;

    [SkippableFact]
    public async Task ListRevisions_Restore_RoundTrip()
    {
        TestSkip.IfUnavailable(_fixture);
        var svc = _fixture.Service!;
        var folder = await _fixture.NewTestFolderAsync(nameof(ListRevisions_Restore_RoundTrip));
        try
        {
            var path = $"{folder}/rev.txt";
            using (var ms = new MemoryStream(Encoding.UTF8.GetBytes("v1")))
                await svc.UploadAsync(path, ms);
            using (var ms = new MemoryStream(Encoding.UTF8.GetBytes("v2 modified")))
                await svc.UploadAsync(path, ms);

            var revisions = await svc.ListRevisionsAsync(path, limit: 10);
            Assert.True(revisions.Count >= 1);

            var oldest = revisions[^1];
            var restored = await svc.RestoreAsync(path, oldest.Rev);
            Assert.Equal("rev.txt", restored.Name);
        }
        finally
        {
            try { await svc.DeleteAsync(folder); } catch { }
        }
    }
}
