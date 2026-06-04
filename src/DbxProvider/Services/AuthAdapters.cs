using System;
using System.IO;
using System.Management.Automation.Host;
using System.Threading;
using System.Threading.Tasks;
using IntelliTect.Dropbox.Auth;
using Microsoft.Playwright;

namespace DbxProvider.Services;

/// <summary>
/// <see cref="IBrowserLauncher"/> that launches a persistent Chromium-family
/// context from the user's detected default-browser executable, reusing a
/// per-user profile directory so sign-in state survives between runs.
///
/// Owns the <see cref="IPlaywright"/> and <see cref="IBrowserContext"/>
/// lifetimes; consumers must not dispose the returned context. Disposing the
/// launcher closes the context and tears down Playwright.
/// </summary>
public sealed class PlaywrightBrowserLauncher : IBrowserLauncher, IAsyncDisposable
{
    private readonly string _executablePath;
    private readonly string _profileDir;
    private IPlaywright? _playwright;
    private IBrowserContext? _context;

    /// <summary>Creates a launcher for the given browser executable.</summary>
    public PlaywrightBrowserLauncher(string executablePath, string? profileDir = null)
    {
        _executablePath = executablePath ?? throw new ArgumentNullException(nameof(executablePath));
        _profileDir = profileDir ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DbxProvider", "playwright-profile");
    }

    /// <inheritdoc />
    public async Task<IBrowserContext> LaunchAsync(CancellationToken ct)
    {
        Directory.CreateDirectory(_profileDir);
        _playwright = await Playwright.CreateAsync().ConfigureAwait(false);
        _context = await _playwright.Chromium.LaunchPersistentContextAsync(_profileDir, new()
        {
            ExecutablePath = _executablePath,
            Headless = false,
            AcceptDownloads = false,
            ViewportSize = ViewportSize.NoViewport,
        }).ConfigureAwait(false);
        return _context;
    }

    /// <summary>Closes the context and disposes Playwright.</summary>
    public async ValueTask DisposeAsync()
    {
        try { if (_context is not null) await _context.CloseAsync().ConfigureAwait(false); }
        catch { /* best-effort */ }
        _playwright?.Dispose();
    }
}

/// <summary>
/// Adapts a PowerShell cmdlet host to <see cref="IConsole"/>, preserving the
/// existing <c>Host.UI.WriteLine</c> / <c>ReadLine</c> behaviour and messages.
/// </summary>
public sealed class CmdletConsole : IConsole
{
    private readonly PSHostUserInterface _ui;

    /// <summary>Creates a console adapter over the supplied host UI.</summary>
    public CmdletConsole(PSHostUserInterface ui)
        => _ui = ui ?? throw new ArgumentNullException(nameof(ui));

    /// <inheritdoc />
    public void Info(string message) => _ui.WriteLine(message);

    /// <inheritdoc />
    public string Prompt(string message)
    {
        _ui.Write(message);
        return _ui.ReadLine() ?? string.Empty;
    }
}
