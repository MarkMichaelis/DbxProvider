using DbxProvider.FunctionalTests.Infrastructure;
using Xunit;

namespace DbxProvider.FunctionalTests.Services;

[Collection("Dropbox")]
public class ExportTests
{
    private readonly DropboxFixture _fixture;
    public ExportTests(DropboxFixture fixture) => _fixture = fixture;

    [SkippableFact]
    public async Task Export_GdocIfAvailable()
    {
        TestSkip.IfUnavailable(_fixture);
        var svc = _fixture.Service!;

        var entries = await svc.ListFolderAsync("/", recursive: true);
        var gdoc = entries.FirstOrDefault(e => !e.IsFolder &&
            (e.Name.EndsWith(".gdoc", StringComparison.OrdinalIgnoreCase) ||
             e.Name.EndsWith(".gsheet", StringComparison.OrdinalIgnoreCase) ||
             e.Name.EndsWith(".gslides", StringComparison.OrdinalIgnoreCase)));

        Skip.If(gdoc == null, "No exportable Google Docs file in account.");

        var (bytes, meta) = await svc.ExportFileAsync(gdoc!.Path);
        Assert.NotNull(bytes);
        Assert.True(bytes.Length > 0);
        Assert.False(string.IsNullOrWhiteSpace(meta.Name));
    }
}
