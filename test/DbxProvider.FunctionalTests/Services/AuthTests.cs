using DbxProvider.FunctionalTests.Infrastructure;
using Xunit;

namespace DbxProvider.FunctionalTests.Services;

[Collection("Dropbox")]
public class AuthTests
{
    private readonly DropboxFixture _fixture;
    public AuthTests(DropboxFixture fixture) => _fixture = fixture;

    [SkippableFact]
    public async Task RefreshTokenCtor_GetCurrentAccount_ReturnsAccountInfo()
    {
        TestSkip.IfUnavailable(_fixture);

        var account = await _fixture.Service!.GetCurrentAccountAsync();

        Assert.False(string.IsNullOrWhiteSpace(account.Email));
        Assert.False(string.IsNullOrWhiteSpace(account.DisplayName));
        Assert.False(string.IsNullOrWhiteSpace(account.AccountId));
    }
}
