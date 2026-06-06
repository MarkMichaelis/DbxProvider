using IntelliTect.Dropbox;
using Xunit;

namespace DbxProvider.FunctionalTests.Services;

/// <summary>
/// Round-trip and resilience behavior for <see cref="CacheConfigStore"/>, the
/// PowerShell-agnostic persistence layer for cache settings. Tests redirect the
/// config root to a temp directory so the real %LOCALAPPDATA% is never touched.
/// </summary>
public class CacheConfigStoreTests : IDisposable
{
    private readonly string _root;

    public CacheConfigStoreTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "DbxCacheConfigTests-" + Guid.NewGuid().ToString("N"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            try { Directory.Delete(_root, recursive: true); } catch { }
        }
    }

    [Fact]
    public void Save_then_load_round_trips_overrides_case_insensitively()
    {
        var store = new CacheConfigStore(_root);
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["user@example.com"] = @"C:\cache\u.db",
        };

        store.SaveOverrides(map);
        var loaded = store.LoadOverrides();

        Assert.Equal(@"C:\cache\u.db", loaded["USER@EXAMPLE.COM"]);
    }

    [Fact]
    public void Save_writes_config_json_under_the_configured_root()
    {
        var store = new CacheConfigStore(_root);

        store.SaveOverrides(new Dictionary<string, string> { ["a@b.com"] = "x.db" });

        Assert.True(File.Exists(Path.Combine(_root, "config.json")));
    }

    [Fact]
    public void Load_missing_file_returns_empty_without_throwing()
    {
        var store = new CacheConfigStore(Path.Combine(_root, "does-not-exist"));

        var loaded = store.LoadOverrides();

        Assert.Empty(loaded);
    }

    [Fact]
    public void Load_corrupt_file_returns_empty_without_throwing()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "config.json"), "{ this is not valid json ");
        var store = new CacheConfigStore(_root);

        var loaded = store.LoadOverrides();

        Assert.Empty(loaded);
    }
}