using System.Security.Cryptography;
using System.Text;
using IntelliTect.Dropbox;
using Xunit;

namespace DbxProvider.FunctionalTests.Services;

/// <summary>
/// Path/naming behavior for the metadata cache database file. These tests do
/// not require Dropbox connectivity: they construct a client with a fake token
/// and only inspect the computed on-disk paths, so they run everywhere.
/// </summary>
public class CachePathTests : IDisposable
{
    private readonly string _root;
    private readonly DropboxServiceClient _service;

    public CachePathTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "DbxCachePathTests-" + Guid.NewGuid().ToString("N"));
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
    public void Email_named_db_lives_directly_in_cache_root_with_no_subfolder()
    {
        using var cache = new MetadataCache(_service, "acct-1", "User@Example.com", Opts());

        var expected = Path.Combine(_root, "DropboxCache.user@example.com.db");
        Assert.Equal(expected, cache.DatabasePath);
        Assert.True(File.Exists(cache.DatabasePath), "Database file should be created directly in the cache root.");
        Assert.Equal(_root, cache.AccountDirectory);
        Assert.Empty(Directory.GetDirectories(_root));
    }

    [Fact]
    public void Default_root_is_DbxProvider_directory_with_no_cache_subfolder()
    {
        // No RootDirectoryOverride: exercise the real default composition.
        var options = new CacheOptions();

        var expected = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DbxProvider");

        Assert.Equal(expected, options.EffectiveRootDirectory);
        Assert.Equal(expected, CacheOptions.Default.EffectiveRootDirectory);
        var segments = options.EffectiveRootDirectory.Split(
            Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        Assert.DoesNotContain("cache", segments);
    }

    [Fact]
    public void Default_database_path_lives_directly_under_DbxProvider_with_no_cache_segment()
    {
        // No RootDirectoryOverride: the resolved default DB path must drop the
        // redundant "cache" segment and sit directly under DbxProvider.
        var options = new CacheOptions { FlushIntervalSeconds = 0 };

        var resolved = MetadataCache.GetDatabasePath(options, "User@Example.com", "acct-1");

        var expected = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DbxProvider",
            "DropboxCache.user@example.com.db");
        Assert.Equal(expected, resolved);
        var segments = resolved.Split(
            Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        Assert.DoesNotContain("cache", segments);
    }

    [Fact]
    public void Invalid_filename_character_in_email_is_sanitized_to_underscore()
    {
        var invalid = Path.GetInvalidFileNameChars().First(c => !char.IsControl(c) && c != '_');
        var email = "a" + invalid + "b@example.com";

        using var cache = new MetadataCache(_service, "acct-1", email, Opts());

        var expected = Path.Combine(_root, "DropboxCache.a_b@example.com.db");
        Assert.Equal(expected, cache.DatabasePath);
    }

    [Fact]
    public void Empty_email_falls_back_to_account_id_hash_named_db()
    {
        using var cache = new MetadataCache(_service, "acct-XYZ", "", Opts());

        var expected = Path.Combine(_root, "DropboxCache." + Sha256Hex("acct-XYZ") + ".db");
        Assert.Equal(expected, cache.DatabasePath);
        Assert.Equal(_root, cache.AccountDirectory);
        Assert.Empty(Directory.GetDirectories(_root));
    }

    private static string Sha256Hex(string input)
    {
        using var sha = SHA256.Create();
        var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(input));
        return BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant();
    }
}