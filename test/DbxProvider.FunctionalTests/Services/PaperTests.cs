using System.Text;
using DbxProvider.FunctionalTests.Infrastructure;
using Xunit;

namespace DbxProvider.FunctionalTests.Services;

[Collection("Dropbox")]
public class PaperTests
{
    private readonly DropboxFixture _fixture;
    public PaperTests(DropboxFixture fixture) => _fixture = fixture;

    [SkippableFact]
    public async Task CreateAndUpdate_PaperDoc()
    {
        TestSkip.IfUnavailable(_fixture);
        var svc = _fixture.Service!;
        var folder = await _fixture.NewTestFolderAsync(nameof(CreateAndUpdate_PaperDoc));
        try
        {
            var path = $"{folder}/doc.paper";
            var html1 = Encoding.UTF8.GetBytes("<h1>Hello</h1><p>v1</p>");
            var html2 = Encoding.UTF8.GetBytes("<h1>Hello</h1><p>v2</p>");

            try
            {
                var url = await svc.CreatePaperDocAsync(path, html1, "html");
                Assert.False(string.IsNullOrWhiteSpace(url));

                var rev = await svc.UpdatePaperDocAsync(path, html2, "html", "overwrite");
                Assert.False(string.IsNullOrWhiteSpace(rev));
            }
            catch (Exception ex) when (
                ex.Message.Contains("deprecated", StringComparison.OrdinalIgnoreCase) ||
                ex.Message.Contains("disabled", StringComparison.OrdinalIgnoreCase) ||
                ex.Message.Contains("not_allowed", StringComparison.OrdinalIgnoreCase) ||
                ex.Message.Contains("paper_disabled", StringComparison.OrdinalIgnoreCase))
            {
                Skip.If(true, $"Paper API unavailable: {ex.Message}");
            }
        }
        finally
        {
            try { await svc.DeleteAsync(folder); } catch { }
        }
    }
}
