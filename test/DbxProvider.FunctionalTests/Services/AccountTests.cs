using DbxProvider.FunctionalTests.Infrastructure;
using Xunit;

namespace DbxProvider.FunctionalTests.Services;

[Collection("Dropbox")]
public class AccountTests
{
    private readonly DropboxFixture _fixture;
    public AccountTests(DropboxFixture fixture) => _fixture = fixture;

    [SkippableFact]
    public async Task GetCurrentAccount_ReturnsAccount()
    {
        TestSkip.IfUnavailable(_fixture);
        var account = await _fixture.Service!.GetCurrentAccountAsync();
        Assert.False(string.IsNullOrWhiteSpace(account.AccountId));
        Assert.False(string.IsNullOrWhiteSpace(account.Email));
    }

    [SkippableFact]
    public async Task GetSpaceUsage_ReturnsValues()
    {
        TestSkip.IfUnavailable(_fixture);
        var usage = await _fixture.Service!.GetSpaceUsageAsync();
        Assert.True(usage.Allocated > 0);
        Assert.True(usage.Used >= 0);
    }
}
