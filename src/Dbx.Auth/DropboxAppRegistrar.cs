using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Playwright;

namespace MarkMichaelis.Dropbox.Auth;

/// <summary>
/// Drives the Dropbox App Console form via Playwright using a browser context
/// supplied by an <see cref="IBrowserLauncher"/>, then reads the resulting
/// App key out of the DOM.
///
/// Two phases:
///  - <b>Phase A (pre-create)</b>: navigate to the create-app page, pre-fill
///    "Scoped access" + "Full Dropbox" + a generated app name, then wait for
///    the user to click <i>Create app</i> (detected by URL change).
///  - <b>Phase B (post-create)</b>: add the redirect URI on the Settings tab,
///    check the required scopes on the Permissions tab, and read the App key
///    value back from the Settings tab DOM.
///
/// On any failure (selector miss, navigation timeout, user cancel) we save a
/// Playwright trace under <c>%TEMP%</c> and return <c>null</c> so the caller
/// can fall through to a manual wizard.
///
/// Selectors last verified: 2026-05-30. If Dropbox redesigns the App Console,
/// the registrar fails fast and the caller's manual fallback takes over.
/// </summary>
public sealed class DropboxAppRegistrar
{
    /// <summary>Result of a successful auto-registration.</summary>
    /// <param name="AppKey">The captured Dropbox app key.</param>
    /// <param name="AppSecret">The captured app secret, when visible.</param>
    /// <param name="AppName">The generated app name that was submitted.</param>
    public sealed record Result(string AppKey, string? AppSecret, string AppName);

    private static readonly char[] AppNameAlphabet =
        "abcdefghijklmnopqrstuvwxyz0123456789".ToCharArray();

    private readonly IBrowserLauncher _launcher;
    private readonly IConsole _console;

    /// <summary>
    /// Creates a registrar that drives the supplied <paramref name="launcher"/>'s
    /// browser context and reports progress through <paramref name="console"/>.
    /// </summary>
    public DropboxAppRegistrar(IBrowserLauncher launcher, IConsole console)
    {
        _launcher = launcher ?? throw new ArgumentNullException(nameof(launcher));
        _console = console ?? throw new ArgumentNullException(nameof(console));
    }

    /// <summary>
    /// Cryptographically-random app name in the form
    /// <c>PSDbxProvider-&lt;8 alnum&gt;</c>. The <c>PS</c> prefix signals
    /// PowerShell origin to anyone scanning the App Console list (the App
    /// Console has no PowerShell context, unlike PSGallery).
    /// </summary>
    public static string GenerateAppName()
    {
        var bytes = new byte[8];
        using (var rng = RandomNumberGenerator.Create())
        {
            rng.GetBytes(bytes);
        }
        var sb = new StringBuilder("PSDbxProvider-", 22);
        for (int i = 0; i < bytes.Length; i++)
        {
            sb.Append(AppNameAlphabet[bytes[i] % AppNameAlphabet.Length]);
        }
        return sb.ToString();
    }

    /// <summary>
    /// Drives the App Console form end-to-end. Returns <c>null</c> on any
    /// failure; the caller falls back to a manual wizard.
    /// </summary>
    /// <param name="redirectUri">The loopback redirect URI to register.</param>
    /// <param name="scopes">The OAuth scopes to enable on the Permissions tab.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<Result?> RegisterAsync(
        string redirectUri,
        IReadOnlyList<string> scopes,
        CancellationToken ct)
    {
        var suggestedName = GenerateAppName();

        IBrowserContext? context = null;
        try
        {
            // The launcher owns the context lifetime; we must not dispose it.
            context = await _launcher.LaunchAsync(ct).ConfigureAwait(false);

            await context.Tracing.StartAsync(new() { Screenshots = true, Snapshots = true }).ConfigureAwait(false);

            var page = context.Pages.FirstOrDefault() ?? await context.NewPageAsync().ConfigureAwait(false);

            // ---- Phase A: pre-fill the Create page and wait for the user click. ----
            await page.GotoAsync("https://www.dropbox.com/developers/apps/create",
                new() { WaitUntil = WaitUntilState.DOMContentLoaded }).ConfigureAwait(false);

            // The user may need to sign in first. Poll up to 5 minutes for the
            // create-app form to actually be visible.
            await WaitForCreateFormAsync(page, TimeSpan.FromMinutes(5), ct).ConfigureAwait(false);

            await SelectRadioByLabelAsync(page, "Scoped access").ConfigureAwait(false);
            await SelectRadioByLabelAsync(page, "Full Dropbox").ConfigureAwait(false);

            var nameField = page.GetByLabel(new Regex(
                @"name your app|app name", RegexOptions.IgnoreCase));
            await nameField.First.FillAsync(suggestedName).ConfigureAwait(false);

            _console.Info($"Form pre-filled with name '{suggestedName}'. Click 'Create app' in the browser to continue.");

            // Wait for navigation to /developers/apps/<id>.
            await page.WaitForURLAsync(
                url => url.IndexOf("/developers/apps/", StringComparison.OrdinalIgnoreCase) >= 0
                       && !url.EndsWith("/create", StringComparison.OrdinalIgnoreCase),
                new() { Timeout = (float)TimeSpan.FromMinutes(10).TotalMilliseconds }
            ).ConfigureAwait(false);

            // ---- Phase B: automated post-create config. ----
            // Settings tab: add redirect URI.
            await GoToSettingsTabAsync(page).ConfigureAwait(false);

            var redirectInput = page.GetByPlaceholder(new Regex(
                "redirect uri|https://", RegexOptions.IgnoreCase));
            await redirectInput.First.FillAsync(redirectUri).ConfigureAwait(false);
            await page.GetByRole(AriaRole.Button, new() { Name = "Add" }).First.ClickAsync().ConfigureAwait(false);
            await page.WaitForTimeoutAsync(400).ConfigureAwait(false);

            // Permissions tab: check scopes, click Submit.
            await GoToTabAsync(page, "Permissions").ConfigureAwait(false);
            foreach (var scope in scopes)
            {
                var box = page.GetByLabel(scope, new() { Exact = true });
                if (await box.CountAsync().ConfigureAwait(false) == 0)
                {
                    // Fall back to a role-based locator.
                    box = page.GetByRole(AriaRole.Checkbox, new() { Name = scope });
                }
                await box.First.CheckAsync().ConfigureAwait(false);
                await page.WaitForTimeoutAsync(150).ConfigureAwait(false);
            }
            await page.GetByRole(AriaRole.Button, new() { Name = "Submit" }).First.ClickAsync().ConfigureAwait(false);
            await page.WaitForTimeoutAsync(800).ConfigureAwait(false);

            // Settings tab: read App key (and App secret if visible).
            await GoToSettingsTabAsync(page).ConfigureAwait(false);
            var appKey = await ReadFieldByLabelAsync(page, "App key").ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(appKey))
            {
                throw new InvalidOperationException("Could not read App key from Settings tab.");
            }
            var appSecret = await TryReadFieldByLabelAsync(page, "App secret").ConfigureAwait(false);

            await context.Tracing.StopAsync().ConfigureAwait(false);
            return new Result(appKey!.Trim(), string.IsNullOrWhiteSpace(appSecret) ? null : appSecret!.Trim(), suggestedName);
        }
        catch (Exception ex)
        {
            try
            {
                if (context is not null)
                {
                    var tracePath = Path.Combine(Path.GetTempPath(),
                        $"dbxprovider-trace-{DateTime.UtcNow:yyyyMMddHHmmss}.zip");
                    await context.Tracing.StopAsync(new() { Path = tracePath }).ConfigureAwait(false);
                    _console.Info($"Auto-registrar failed: {ex.Message}. Trace saved to {tracePath}.");
                }
                else
                {
                    _console.Info($"Auto-registrar failed before context creation: {ex.Message}.");
                }
            }
            catch
            {
                // ignore trace failures
            }
            return null;
        }
    }

    private static async Task WaitForCreateFormAsync(IPage page, TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            var url = page.Url;
            if (url.IndexOf("/developers/apps/create", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                // Look for any of the expected radio labels.
                var probe = page.GetByText(new Regex(
                    "scoped access", RegexOptions.IgnoreCase));
                if (await probe.CountAsync().ConfigureAwait(false) > 0)
                {
                    return;
                }
            }
            await Task.Delay(1000, ct).ConfigureAwait(false);
        }
        throw new TimeoutException("Timed out waiting for the Dropbox create-app form (sign-in required?).");
    }

    private static async Task SelectRadioByLabelAsync(IPage page, string label)
    {
        var radio = page.GetByLabel(label, new() { Exact = false });
        await radio.First.CheckAsync().ConfigureAwait(false);
    }

    private static async Task GoToTabAsync(IPage page, string tabName)
    {
        var tab = page.GetByRole(AriaRole.Tab, new() { Name = tabName });
        if (await tab.CountAsync().ConfigureAwait(false) == 0)
        {
            tab = page.GetByRole(AriaRole.Link, new() { Name = tabName });
        }
        await tab.First.ClickAsync().ConfigureAwait(false);
        await page.WaitForTimeoutAsync(400).ConfigureAwait(false);
    }

    private static Task GoToSettingsTabAsync(IPage page) => GoToTabAsync(page, "Settings");

    private static async Task<string?> TryReadFieldByLabelAsync(IPage page, string label)
    {
        try { return await ReadFieldByLabelAsync(page, label).ConfigureAwait(false); }
        catch { return null; }
    }

    private static async Task<string?> ReadFieldByLabelAsync(IPage page, string label)
    {
        // The App key is rendered as a read-only input or as text next to the
        // label. Try input first, then sibling text.
        var input = page.GetByLabel(label, new() { Exact = false });
        if (await input.CountAsync().ConfigureAwait(false) > 0)
        {
            try
            {
                var v = await input.First.InputValueAsync().ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(v)) return v;
            }
            catch { /* not an input  fall through */ }
            try
            {
                var t = await input.First.InnerTextAsync().ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(t)) return t;
            }
            catch { }
        }

        // Fallback: find a row whose first cell text matches the label.
        var row = page.Locator($"tr:has-text(\"{label}\")");
        if (await row.CountAsync().ConfigureAwait(false) > 0)
        {
            return Regex.Replace(
                    await row.First.InnerTextAsync().ConfigureAwait(false),
                    Regex.Escape(label), string.Empty, RegexOptions.IgnoreCase)
                .Trim();
        }
        return null;
    }
}
