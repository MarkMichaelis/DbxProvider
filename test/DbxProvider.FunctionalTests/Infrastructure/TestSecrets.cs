using IntelliTect.Dropbox;
using Microsoft.Extensions.Configuration;

namespace DbxProvider.FunctionalTests.Infrastructure;

/// <summary>
/// Resolves Dropbox test credentials from (in order):
///   1. Environment variables (DBX_APP_KEY, DBX_APP_SECRET, DBX_REFRESH_TOKEN, ...)
///   2. dotnet user-secrets keyed under the same names
///   3. The shared CredentialStore (%LOCALAPPDATA%\DbxProvider\credentials.json)
///      populated by a normal Connect-Dropbox -AppKey ... call.
/// The CredentialStore fallback removes the need for a separate refresh-token
/// bootstrap helper: developers just run Connect-Dropbox once.
/// </summary>
public static class TestSecrets
{
    private static readonly Lazy<IConfiguration> _config = new(BuildConfiguration);
    private static readonly Lazy<StoredCredentials?> _stored = new(() =>
    {
        try { return CredentialStore.Load(); } catch { return null; }
    });

    private static IConfiguration BuildConfiguration()
    {
        var builder = new ConfigurationBuilder()
            .AddUserSecrets<DropboxFixture>(optional: true)
            .AddEnvironmentVariables();
        return builder.Build();
    }

    private static string? Get(string envName, string configKey, Func<StoredCredentials, string?>? fromStore = null)
    {
        var v = Environment.GetEnvironmentVariable(envName);
        if (!string.IsNullOrWhiteSpace(v)) return v;
        v = _config.Value[configKey];
        if (!string.IsNullOrWhiteSpace(v)) return v;
        if (fromStore is not null && _stored.Value is { } s)
        {
            v = fromStore(s);
            if (!string.IsNullOrWhiteSpace(v)) return v;
        }
        return null;
    }

    public static string? AppKey => Get("DBX_APP_KEY", "DBX_APP_KEY", s => s.AppKey);
    public static string? AppSecret => Get("DBX_APP_SECRET", "DBX_APP_SECRET", s => s.AppSecret);
    public static string? RefreshToken => Get("DBX_REFRESH_TOKEN", "DBX_REFRESH_TOKEN", s => s.RefreshToken);
    public static string? TestMemberEmail => Get("DBX_TEST_MEMBER_EMAIL", "DBX_TEST_MEMBER_EMAIL");

    public static bool HasCoreCredentials =>
        !string.IsNullOrWhiteSpace(AppKey) &&
        !string.IsNullOrWhiteSpace(AppSecret) &&
        !string.IsNullOrWhiteSpace(RefreshToken);
}
