using System;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace DbxProvider.Services;

/// <summary>
/// Detects the user's default web browser on Windows by walking the
/// <c>UrlAssociations\http\UserChoice</c> registry chain, then resolves the
/// associated executable path via <c>HKEY_CLASSES_ROOT\&lt;ProgId&gt;\shell\open\command</c>.
///
/// The detection is split into:
///  - <see cref="Detect"/> — touches the live registry, Windows-only.
///  - <see cref="Map"/>     — pure function from <c>(progId, rawCommand)</c> to
///                            <see cref="Result"/>; unit-testable without
///                            registry access.
///
/// On non-Windows platforms, <see cref="Detect"/> returns
/// <c>(null, "unknown", false)</c> so callers fall back to the manual wizard.
/// </summary>
internal static class DefaultBrowser
{
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

    public static Result Detect()
    {
        if (!OperatingSystem.IsWindows())
        {
            return new Result(null, "unknown", false);
        }

        return DetectWindows();
    }

    [SupportedOSPlatform("windows")]
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
            // Any registry/permissions hiccup ⇒ unknown ⇒ manual fallback.
            return new Result(null, "unknown", false);
        }
    }

    /// <summary>
    /// Pure mapper used by <see cref="Detect"/> and exposed for unit tests.
    /// </summary>
    internal static Result Map(string? progId, string? rawCommand)
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

        raw = raw.Trim();
        if (raw.StartsWith("\""))
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
