using System.Text;
using DbxProvider.FunctionalTests.Infrastructure;
using Xunit;

namespace DbxProvider.FunctionalTests.Services;

[Collection("Dropbox")]
public class MemberTests
{
    private readonly DropboxFixture _fixture;
    public MemberTests(DropboxFixture fixture) => _fixture = fixture;

    [SkippableFact]
    public async Task FolderMember_AddListRemove()
    {
        TestSkip.IfUnavailable(_fixture);
        Skip.If(string.IsNullOrWhiteSpace(TestSecrets.TestMemberEmail), "DBX_TEST_MEMBER_EMAIL not configured.");

        var svc = _fixture.Service!;
        var email = TestSecrets.TestMemberEmail!;
        var folder = await _fixture.NewTestFolderAsync(nameof(FolderMember_AddListRemove));
        string? sharedId = null;
        try
        {
            sharedId = await svc.ShareFolderAsync(folder);
            await svc.AddFolderMemberAsync(sharedId, email);

            await Task.Delay(2000);
            var members = await svc.ListFolderMembersAsync(sharedId);
            Assert.Contains(members, m => string.Equals(m.Email, email, StringComparison.OrdinalIgnoreCase));

            await svc.RemoveFolderMemberAsync(sharedId, email);
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

    [SkippableFact]
    public async Task FileMember_AddListRemove()
    {
        TestSkip.IfUnavailable(_fixture);
        Skip.If(string.IsNullOrWhiteSpace(TestSecrets.TestMemberEmail), "DBX_TEST_MEMBER_EMAIL not configured.");

        var svc = _fixture.Service!;
        var email = TestSecrets.TestMemberEmail!;
        var folder = await _fixture.NewTestFolderAsync(nameof(FileMember_AddListRemove));
        try
        {
            var file = $"{folder}/shared-file.txt";
            using (var ms = new MemoryStream(Encoding.UTF8.GetBytes("file member test")))
                await svc.UploadAsync(file, ms);

            await svc.AddFileMemberAsync(file, email);

            await Task.Delay(2000);
            var members = await svc.ListFileMembersAsync(file);
            Assert.Contains(members, m => string.Equals(m.Email, email, StringComparison.OrdinalIgnoreCase));

            await svc.RemoveFileMemberAsync(file, email);
        }
        finally
        {
            try { await svc.DeleteAsync(folder); } catch { }
        }
    }
}
