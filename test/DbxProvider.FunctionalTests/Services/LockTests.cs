using System.Text;
using DbxProvider.FunctionalTests.Infrastructure;
using Dropbox.Api;
using Xunit;

namespace DbxProvider.FunctionalTests.Services;

[Collection("Dropbox")]
public class LockTests
{
    private readonly DropboxFixture _fixture;
    public LockTests(DropboxFixture fixture) => _fixture = fixture;

    [SkippableFact]
    public async Task Lock_GetLock_Unlock_Batch()
    {
        TestSkip.IfUnavailable(_fixture);
        var svc = _fixture.Service!;
        var folder = await _fixture.NewTestFolderAsync(nameof(Lock_GetLock_Unlock_Batch));
        try
        {
            var path = $"{folder}/lockable.txt";
            using (var ms = new MemoryStream(Encoding.UTF8.GetBytes("lock me")))
                await svc.UploadAsync(path, ms);

            try
            {
                var locked = await svc.LockFilesAsync(path);
                Assert.NotEmpty(locked);

                var got = await svc.GetFileLocksAsync(path);
                Assert.NotEmpty(got);

                var unlocked = await svc.UnlockFilesAsync(path);
                Assert.NotEmpty(unlocked);
            }
            catch (AccessException ex) { TestSkip.OnAccessException(ex); }
            catch (AuthException ex) { TestSkip.OnMissingScope(ex); }
        }
        finally
        {
            try { await svc.DeleteAsync(folder); } catch { }
        }
    }
}
