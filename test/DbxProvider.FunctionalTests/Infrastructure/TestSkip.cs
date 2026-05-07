using Dropbox.Api;
using Xunit;

namespace DbxProvider.FunctionalTests.Infrastructure;

public static class TestSkip
{
    public static void IfUnavailable(DropboxFixture fixture)
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason ?? "Dropbox credentials not configured");
    }

    /// <summary>
    /// If <paramref name="ex"/> is a Dropbox missing_scope or known feature-unavailable
    /// error, mark the current test as Skipped with a descriptive reason. Otherwise
    /// rethrows.
    /// </summary>
    public static void OnMissingScope(Exception ex)
    {
        var msg = ex.Message ?? string.Empty;
        if (msg.Contains("missing_scope", StringComparison.OrdinalIgnoreCase))
        {
            Skip.If(true,
                "Dropbox app token is missing the required scope for this test. " +
                "Grant it in the App Console and re-run Connect-Dropbox to mint a new refresh token. " +
                $"(Original error: {msg})");
            return;
        }
        throw ex;
    }

    /// <summary>
    /// Skip on transient/unexpected Dropbox RetryException (e.g. save_url returning
    /// 'unexpected error occurred' for a particular source URL).
    /// </summary>
    public static void OnRetry(RetryException ex)
    {
        Skip.If(true, $"Dropbox returned a transient error; skipping this run. ({ex.Message})");
    }
}
