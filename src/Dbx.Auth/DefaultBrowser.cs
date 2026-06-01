using System;
#if NET
using System.Runtime.Versioning;
#else
using System.Runtime.InteropServices;
#endif
using Microsoft.Win32;

namespace MarkMichaelis.Dropbox.Auth;

/// <summary>
/// Detects the user's default web browser on Windows by walking the
/// <c>UrlAssociations\http\UserChoice</c> registry chain, then resolves the
/// associated executable path via <c>HKEY_CLASSES_ROOT\&lt;ProgId&gt;\shell\open\command</c>.
///
/// The detection is split into:
///  - <see cref="Detect"/>  touches the live registry, Windows-only.
///  - <see cref="Map"/>      pure function from <c>(progId, rawCommand)</c> to
///                            <see cref="Result"/>; unit-testable without
///                            registry access.
///
/// On non-Windows platforms, <see cref="Detect"/> returns
/// <c>(null, "unknown", false)</c> so callers fall back to the manual wizard.
/// </summary>
public static class DefaultBrowser
{
    /// <summary>Outcome of a default-browser probe.</summary>
    /// <param name="ExecutablePath">Full path to the browser executable, or <c>null</c>.</param>
    /// <param name="FriendlyName">Human-friendly browser name, or <c>"unknown"</c>.</param>
    /// <param name="IsChromiumFamily">Whether Playwright can drive it via the Chromium channel.</param>
    public readonly record struct Result(
        string? ExecutablePath,
        string FriendlyName,
        bool IsChromiumFamily);

    private static readonly (string ProgId, string Friendly, bool Chromium)[] KnownBrowsers =
    {
        ("MSEdgeHTM",   "Microsoft Edge", true),
        ("ChromeHTML",  "Google Chrome",  true),
        ("BraveHTML",   "Brave",          true),
        ("VivaldiHTM",  "Vivaldi",        true),
        ("OperaStable", "Opera",          true),
        ("ArcHTM",      "Arc",            true),
        ("FirefoxURL",  "Firefox",        false),
    };

#if !NET
    private static bool IsWindows() =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
#endif

    /// <summary>
    /// Probes the live registry for the default browser. Returns
    /// <c>(null, "unknown", false)</c> on non-Windows platforms or on any error.
    /// </summary>
    public static Result Detect()
    {
#if NET
        if (!OperatingSystem.IsWindows())
#else
        if (!IsWindows())
#endif
        {
            return new Result(null, "unknown", false);
        }

        return DetectWindows();
    }

#if NET
    [SupportedOSPlatform("windows")]
#endif
    private static Result DetectWindows()
    {
        try
        {
            string? progId = null;
            using (var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\Shell\Associations\UrlAssociations\http\UserChoice"))
            {
                progId = key?.GetValue("ProgId") as string;
            }

            string? rawCommand = null;
            if (!string.IsNullOrEmpty(progId))
            {
                using var cmd = Registry.ClassesRoot.OpenSubKey($@"{progId}\shell\open\command");
                rawCommand = cmd?.GetValue(null) as string;
            }

            return Map(progId, rawCommand);
        }
        catch
        {
            // Any registry/permissions hiccup => unknown => manual fallback.
            return new Result(null, "unknown", false);
        }
    }

    /// <summary>
    /// Pure mapper from a registry ProgId + raw shell command to a
    /// <see cref="Result"/>. Exposed for unit testing without registry access.
    /// </summary>
    /// <param name="progId">The <c>UserChoice</c> ProgId, e.g. <c>ChromeHTML</c>.</param>
    /// <param name="rawCommand">The raw <c>shell\open\command</c> string.</param>
    public static Result Map(string? progId, string? rawCommand)
    {
        if (string.IsNullOrWhiteSpace(progId))
        {
            return new Result(null, "unknown", false);
        }

        string friendly = "unknown";
        bool chromium = false;
        bool known = false;

        foreach (var (id, f, c) in KnownBrowsers)
        {
            if (string.Equals(id, progId, StringComparison.OrdinalIgnoreCase))
            {
                friendly = f;
                chromium = c;
                known = true;
                break;
            }
        }

        if (!known)
        {
            return new Result(null, "unknown", false);
        }

        return new Result(ParseExePath(rawCommand), friendly, chromium);
    }

    private static string? ParseExePath(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        raw = raw!.Trim();
        if (raw.StartsWith("\"", StringComparison.Ordinal))
        {
            int end = raw.IndexOf('"', 1);
            if (end > 1)
            {
                return raw.Substring(1, end - 1);
            }
            return null;
        }

        // Unquoted: take everything up to the first whitespace.
        int ws = raw.IndexOf(' ');
        return ws > 0 ? raw.Substring(0, ws) : raw;
    }
}
