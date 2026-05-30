using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Playwright;

namespace DbxProvider.Services;

/// <summary>
/// Drives the Dropbox App Console form via Playwright using the user's
/// already-installed Chromium-family browser, then reads the resulting
/// App key out of the DOM.
///
/// Two phases:
///  - <b>Phase A (pre-create)</b>: navigate to the create-app page, pre-fill
///    "Scoped access" + "Full Dropbox" + a generated app name, then wait for
///    the user to click <i>Create app</i> (detected by URL change).
///  - <b>Phase B (post-create)</b>: add the redirect URI on the Settings tab,
///    check the seven required scopes on the Permissions tab, and read the
///    App key value back from the Settings tab DOM.
///
/// On any failure (selector miss, navigation timeout, user cancel) we save a
/// Playwright trace under <c>%TEMP%</c> and return <c>null</c> so the caller
/// can fall through to the existing manual wizard.
///
/// Selectors last verified: 2026-05-30. If Dropbox redesigns the App Console,
/// the registrar fails fast and the manual wizard takes over — no regression.
/// </summary>
internal sealed class DropboxAppRegistrar
{
    /// <summary>Result of a successful auto-registration.</summary>
    public sealed record Result(string AppKey, string? AppSecret, string AppName);

    private static readonly char[] AppNameAlphabet =
        "abcdefghijklmnopqrstuvwxyz0123456789".ToCharArray();

    private readonly string _executablePath;
    private readonly Action<string> _log;

    public DropboxAppRegistrar(string executablePath, Action<string>? log = null)
    {
        _executablePath = executablePath ?? throw new ArgumentNullException(nameof(executablePath));
        _log = log ?? (_ => { });
    }

    /// <summary>
    /// Cryptographically-random app name in the form
    /// <c>PSDbxProvider-&lt;8 alnum&gt;</c>. The <c>PS</c> prefix signals
    /// PowerShell origin to anyone scanning the App Console list (the App
    /// Console has no PowerShell context, unlike PSGallery).
    /// </summary>
    public static string GenerateAppName()
    {
        Span<byte> bytes = stackalloc byte[8];
        RandomNumberGenerator.Fill(bytes);
        var sb = new StringBuilder("PSDbxProvider-", 22);
        for (int i = 0; i < bytes.Length; i++)
        {
            sb.Append(AppNameAlphabet[bytes[i] % AppNameAlphabet.Length]);
        }
        return sb.ToString();
    }

    /// <summary>
    /// Drives the App Console form end-to-end. Returns <c>null</c> on any
    /// failure; the caller falls back to the manual wizard.
    /// </summary>
    public async Task<Result?> RegisterAsync(
        string redirectUri,
        IReadOnlyList<string> scopes,
        CancellationToken ct)
    {
        var suggestedName = GenerateAppName();
        var profileDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DbxProvider", "playwright-profile");
        Directory.CreateDirectory(profileDir);

        IPlaywright? pw = null;
        IBrowserContext? context = null;
        try
        {
            pw = await Playwright.CreateAsync().ConfigureAwait(false);
            context = await pw.Chromium.LaunchPersistentContextAsync(profileDir, new()
            {
                ExecutablePath = _executablePath,
                Headless = false,
                AcceptDownloads = false,
                ViewportSize = ViewportSize.NoViewport,
            }).ConfigureAwait(false);

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

            var nameField = page.GetByLabel(new System.Text.RegularExpressions.Regex(
                @"name your app|app name", System.Text.RegularExpressions.RegexOptions.IgnoreCase));
            await nameField.First.FillAsync(suggestedName).ConfigureAwait(false);

            _log($"Form pre-filled with name '{suggestedName}'. Click 'Create app' in the browser to continue.");

            // Wait for navigation to /developers/apps/<id>.
            await page.WaitForURLAsync(
                url => url.Contains("/developers/apps/", StringComparison.OrdinalIgnoreCase)
                       && !url.EndsWith("/create", StringComparison.OrdinalIgnoreCase),
                new() { Timeout = (float)TimeSpan.FromMinutes(10).TotalMilliseconds }
            ).ConfigureAwait(false);

            // ---- Phase B: automated post-create config. ----
            // Settings tab: add redirect URI.
            await GoToSettingsTabAsync(page).ConfigureAwait(false);

            var redirectInput = page.GetByPlaceholder(new System.Text.RegularExpressions.Regex(
                "redirect uri|https://", System.Text.RegularExpressions.RegexOptions.IgnoreCase));
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
            return new Result(appKey.Trim(), string.IsNullOrWhiteSpace(appSecret) ? null : appSecret.Trim(), suggestedName);
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
                    _log($"Auto-registrar failed: {ex.Message}. Trace saved to {tracePath}.");
                }
                else
                {
                    _log($"Auto-registrar failed before context creation: {ex.Message}.");
                }
            }
            catch
            {
                // ignore trace failures
            }
            return null;
        }
        finally
        {
            try { if (context is not null) await context.CloseAsync().ConfigureAwait(false); } catch { }
            pw?.Dispose();
        }
    }

    private static async Task WaitForCreateFormAsync(IPage page, TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            var url = page.Url;
            if (url.Contains("/developers/apps/create", StringComparison.OrdinalIgnoreCase))
            {
                // Look for any of the expected radio labels.
                var probe = page.GetByText(new System.Text.RegularExpressions.Regex(
                    "scoped access", System.Text.RegularExpressions.RegexOptions.IgnoreCase));
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
            catch { /* not an input — fall through */ }
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
            return (await row.First.InnerTextAsync().ConfigureAwait(false))
                .Replace(label, string.Empty, StringComparison.OrdinalIgnoreCase)
                .Trim();
        }
        return null;
    }
}
