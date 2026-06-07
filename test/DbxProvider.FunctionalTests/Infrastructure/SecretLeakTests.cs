using System.Diagnostics;
using System.Text.RegularExpressions;
using Xunit;

namespace DbxProvider.FunctionalTests.Infrastructure;

/// <summary>
/// Guard test: scans every non-gitignored file in the repository for accidentally
/// committed Dropbox credentials. Runs without Dropbox connectivity so it always
/// executes (locally and in CI), even on a fresh clone with no secrets configured.
///
/// Detects:
///   - Dropbox short-lived access tokens (sl.&lt;base64&gt;)
///   - CredentialStore-encoded values (dpapi:... / plain:...)
///   - Any AppKey / AppSecret / RefreshToken values currently configured for the
///     test fixture (env vars, user-secrets, or CredentialStore) — catches the
///     case of a developer pasting their own real values into a tracked file.
/// </summary>
public class SecretLeakTests
{
    private static readonly string[] SelfMarkers = new[]
    {
        "SecretLeakTests.cs",
    };

    [Fact]
    public void No_credentials_are_committed_to_the_repository()
    {
        var repoRoot = FindRepoRoot();
        var files = ListNonIgnoredFiles(repoRoot);

        var patterns = new List<(string Name, Regex Re)>
        {
            ("Dropbox short-lived access token (sl.<...>)",
                new Regex(@"\bsl\.[A-Za-z0-9_-]{100,}\b", RegexOptions.Compiled)),
            ("CredentialStore DPAPI-encoded value",
                new Regex(@"""dpapi:[A-Za-z0-9+/=]{20,}""", RegexOptions.Compiled)),
            ("CredentialStore plaintext-base64 value",
                new Regex(@"""plain:[A-Za-z0-9+/=]{20,}""", RegexOptions.Compiled)),
        };

        foreach (var (label, value) in new[]
                 {
                     ("configured AppKey",       TestSecrets.AppKey),
                     ("configured AppSecret",    TestSecrets.AppSecret),
                     ("configured RefreshToken", TestSecrets.RefreshToken),
                 })
        {
            if (!string.IsNullOrWhiteSpace(value) && value.Length >= 10)
            {
                patterns.Add((label, new Regex(Regex.Escape(value), RegexOptions.Compiled)));
            }
        }

        var findings = new List<string>();
        foreach (var rel in files)
        {
            if (SelfMarkers.Any(m => rel.EndsWith(m, StringComparison.OrdinalIgnoreCase)))
                continue;

            var full = Path.Combine(repoRoot, rel);
            if (!File.Exists(full)) continue;
            if (IsLikelyBinary(full)) continue;

            string text;
            try { text = File.ReadAllText(full); }
            catch { continue; }

            foreach (var (name, re) in patterns)
            {
                var m = re.Match(text);
                if (m.Success)
                {
                    var snippet = m.Value.Length > 24 ? m.Value.Substring(0, 12) + "..." : m.Value;
                    findings.Add($"{rel}: {name} matched '{snippet}'");
                }
            }
        }

        Assert.True(findings.Count == 0,
            "Potential Dropbox credentials found in tracked/untracked files:\n  " +
            string.Join("\n  ", findings));
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, ".git")))
            dir = dir.Parent;
        if (dir is null)
            throw new InvalidOperationException("Could not locate repository root (no .git ancestor).");
        return dir.FullName;
    }

    private static List<string> ListNonIgnoredFiles(string repoRoot)
    {
        // -c cached, -o others (untracked), --exclude-standard honors .gitignore + global excludes.
        var psi = new ProcessStartInfo("git", "ls-files -co --exclude-standard")
        {
            WorkingDirectory = repoRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var p = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start git.");
        var stdout = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0)
            throw new InvalidOperationException("git ls-files failed: " + p.StandardError.ReadToEnd());

        return stdout
            .Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim())
            .Where(l => l.Length > 0)
            .ToList();
    }

    private static bool IsLikelyBinary(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        switch (ext)
        {
            case ".dll":
            case ".pdb":
            case ".exe":
            case ".so":
            case ".dylib":
            case ".png":
            case ".jpg":
            case ".jpeg":
            case ".gif":
            case ".ico":
            case ".bmp":
            case ".zip":
            case ".gz":
            case ".7z":
            case ".tar":
            case ".nupkg":
            case ".pfx":
            case ".snk":
                return true;
        }
        try
        {
            using var fs = File.OpenRead(path);
            var buf = new byte[Math.Min(8192, fs.Length)];
            var read = fs.Read(buf, 0, buf.Length);
            for (int i = 0; i < read; i++)
                if (buf[i] == 0) return true;
        }
        catch { return true; }
        return false;
    }
}
