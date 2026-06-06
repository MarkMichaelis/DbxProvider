using IntelliTect.Dropbox;
using Xunit;

namespace DbxProvider.FunctionalTests.Services;

/// <summary>
/// Behavior of the per-email cache database path override. An override entry
/// pins a specific account's metadata cache database to an explicit file path
/// instead of the default name under the cache root. These tests need no
/// Dropbox connectivity: they only inspect computed on-disk paths.
/// </summary>
public class CachePathOverrideTests : IDisposable
{
    private readonly string _root;
    private readonly DropboxServiceClient _service;

    public CachePathOverrideTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "DbxCacheOverrideTests-" + Guid.NewGuid().ToString("N"));
        _service = new DropboxServiceClient("fake-access-token");
    }

    public void Dispose()
    {
        _service.Dispose();
        if (Directory.Exists(_root))
        {
            try { Directory.Delete(_root, recursive: true); } catch { }
        }
    }

    private CacheOptions Opts() =>
        new() { RootDirectoryOverride = _root, FlushIntervalSeconds = 0 };

    [Fact]
    public void Override_for_email_places_db_at_exact_configured_path_case_insensitive()
    {
        // Arrange: store the override under a lower-case email key.
        var dbFile = Path.Combine(_root, "custom", "MyAccount.DropboxCache.db");
        var opts = Opts();
        opts.EmailDatabasePathOverrides["user@example.com"] = dbFile;

        // Act: connect with a differently-cased email.
        using var cache = new MetadataCache(_service, "acct-1", "User@Example.com", opts);

        // Assert: the database is placed at exactly the configured path.
        Assert.Equal(Path.GetFullPath(dbFile), cache.DatabasePath);
        Assert.True(File.Exists(cache.DatabasePath),
            "Override database file should be created at the configured path.");
    }

    [Fact]
    public void Tilde_prefixed_override_expands_under_user_profile_and_is_absolute()
    {
        var opts = Opts();
        opts.EmailDatabasePathOverrides["user@example.com"] = "~/user@example.com.DropboxCache.db";

        // Pure path resolution -- does not create any file in the real home.
        var resolved = MetadataCache.GetDatabasePath(opts, "User@Example.com", "acct-1");

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Assert.StartsWith(home, resolved);
        Assert.True(Path.IsPathRooted(resolved), "Resolved override path must be absolute.");
        Assert.EndsWith("user@example.com.DropboxCache.db", resolved);
    }

    [Fact]
    public void Directory_override_places_default_named_db_inside_it()
    {
        var dir = Path.Combine(_root, "into-here") + Path.DirectorySeparatorChar;
        var opts = Opts();
        opts.EmailDatabasePathOverrides["user@example.com"] = dir;

        using var cache = new MetadataCache(_service, "acct-1", "user@example.com", opts);

        var expected = Path.Combine(Path.GetFullPath(dir), "DropboxCache.user@example.com.db");
        Assert.Equal(expected, cache.DatabasePath);
    }

    [Fact]
    public void No_override_falls_back_to_email_named_db_in_root()
    {
        var opts = Opts();

        using var cache = new MetadataCache(_service, "acct-1", "User@Example.com", opts);

        Assert.Equal(Path.Combine(_root, "DropboxCache.user@example.com.db"), cache.DatabasePath);
    }
}