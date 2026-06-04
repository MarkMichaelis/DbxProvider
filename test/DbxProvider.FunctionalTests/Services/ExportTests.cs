using DbxProvider.FunctionalTests.Infrastructure;
using IntelliTect.Dropbox;
using Xunit;

namespace DbxProvider.FunctionalTests.Services;

[Collection("Dropbox")]
public class ExportTests
{
    // Persistent fixture folder seeded by build/Seed-DbxTestFixtures.ps1.
    // Lives outside /DbxProviderTests (which DropboxFixture wipes on init for
    // ephemeral test data). Tests discover whatever exportable file is
    // present at runtime - the seed script populates a Paper doc by default,
    // but a developer can drop a .gdoc/.gsheet/.gslides via Google Drive
    // sync if Paper isn't available on the account.
    private const string FixtureFolder = "/DbxProviderFixtures";

    private readonly DropboxFixture _fixture;
    public ExportTests(DropboxFixture fixture) => _fixture = fixture;

    [SkippableFact]
    public async Task Export_SeededCloudDoc_RoundTrip()
    {
        TestSkip.IfUnavailable(_fixture);
        var svc = _fixture.Service!;

        List<DropboxItem> items;
        try
        {
            items = await svc.ListFolderAsync(FixtureFolder);
        }
        catch (Exception ex)
        {
            Skip.If(true,
                $"Fixture folder {FixtureFolder} not found ({ex.Message}). " +
                "Run build/Seed-DbxTestFixtures.ps1.");
            return;
        }

        var exportable = items.FirstOrDefault(i => !i.IsFolder && !i.IsDownloadable);
        Skip.If(exportable is null,
            $"No exportable cloud-document in {FixtureFolder}. Drop a " +
            ".gdoc/.gsheet/.gslides via Google Drive sync or 'New Google Docs' " +
            "from the Dropbox web UI, or run build/Seed-DbxTestFixtures.ps1 " +
            "on a Paper-migrated account.");

        var (bytes, meta) = await svc.ExportFileAsync(exportable!.Path);
        Assert.NotNull(bytes);
        Assert.True(bytes.Length > 0);
        Assert.False(string.IsNullOrWhiteSpace(meta.Name));
    }
}
