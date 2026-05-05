using System;

namespace DbxProvider.Services
{
    /// <summary>
    /// Emits OSC 8 terminal hyperlinks (supported by Windows Terminal, iTerm2, GNOME Terminal,
    /// VS Code's integrated terminal, etc.) when the environment looks supportive.
    /// </summary>
    internal static class TerminalHyperlink
    {
        private const char Esc = '\u001b';

        private static readonly Lazy<bool> _supported = new Lazy<bool>(Detect);

        public static bool IsSupported => _supported.Value;

        /// <summary>
        /// Format <paramref name="text"/> as a clickable hyperlink to <paramref name="url"/>
        /// when supported; otherwise returns "<paramref name="text"/> &lt;<paramref name="url"/>&gt;"
        /// (or just the URL if text equals the URL).
        /// </summary>
        public static string Format(string url, string? text = null)
        {
            text ??= url;
            if (!IsSupported)
            {
                return string.Equals(text, url, StringComparison.Ordinal) ? url : $"{text} <{url}>";
            }

            return $"{Esc}]8;;{url}{Esc}\\{text}{Esc}]8;;{Esc}\\";
        }

        private static bool Detect()
        {
            try
            {
                if (Console.IsOutputRedirected) return false;
            }
            catch
            {
                return false;
            }

            // Known supportive environments.
            if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WT_SESSION"))) return true;
            if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("TERM_PROGRAM"))) return true;
            if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ConEmuPID"))) return true;

            var term = Environment.GetEnvironmentVariable("TERM");
            if (!string.IsNullOrEmpty(term) && term != "dumb") return true;

            return false;
        }
    }
}
