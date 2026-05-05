using DbxProvider.Services;
using Xunit;

namespace DbxProvider.FunctionalTests.Infrastructure;

public class DropboxFixture : IAsyncLifetime
{
    public DropboxServiceClient? Service { get; private set; }
    public bool Available { get; private set; }
    public string TestRoot => "/DbxProviderTests";
    public string? SkipReason { get; private set; }

    public async Task InitializeAsync()
    {
        if (!TestSecrets.HasCoreCredentials)
        {
            Available = false;
            SkipReason = "Dropbox credentials not configured (set DBX_APP_KEY, DBX_APP_SECRET, DBX_REFRESH_TOKEN env vars or user-secrets).";
            return;
        }

        try
        {
            Service = new DropboxServiceClient(
                TestSecrets.RefreshToken!,
                TestSecrets.AppKey!,
                TestSecrets.AppSecret!);

            await Service.GetCurrentAccountAsync();

            try
            {
                await Service.DeleteAsync(TestRoot);
            }
            catch
            {
            }

            await Service.CreateFolderAsync(TestRoot);
            Available = true;
        }
        catch (Exception ex)
        {
            Available = false;
            SkipReason = $"Failed to initialize Dropbox client: {ex.Message}";
        }
    }

    public async Task<string> NewTestFolderAsync(string testName)
    {
        if (Service == null) throw new InvalidOperationException("Fixture not available.");
        var path = $"{TestRoot}/{testName}-{Guid.NewGuid():N}";
        await Service.CreateFolderAsync(path);
        return path;
    }

    public Task DisposeAsync()
    {
        Service?.Dispose();
        return Task.CompletedTask;
    }
}

[CollectionDefinition("Dropbox")]
public class DropboxCollection : ICollectionFixture<DropboxFixture>
{
}
