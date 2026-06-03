using System;
using System.Text;
using System.Text.RegularExpressions;

namespace Dbx.Core.Wildcards
{
    /// <summary>
    /// Framework-neutral wildcard matcher that reproduces PowerShell
    /// <c>WildcardPattern</c> (IgnoreCase) semantics without depending on
    /// <c>System.Management.Automation</c>. Supports <c>*</c> (zero or more
    /// characters), <c>?</c> (exactly one character), <c>[set]</c> character
    /// classes with ranges, and backtick (<c>`</c>) escaping. The match is
    /// anchored over the whole input and, by default, case-insensitive.
    /// </summary>
    public sealed class WildcardMatcher
    {
        private const char Escape = '`';
        private readonly Regex _regex;

        /// <summary>
        /// Compiles <paramref name="pattern"/> into an anchored regular
        /// expression mirroring <c>WildcardPattern</c> semantics.
        /// </summary>
        /// <param name="pattern">The wildcard pattern.</param>
        /// <param name="ignoreCase">When true (default), matching is
        /// case-insensitive, matching <c>WildcardOptions.IgnoreCase</c>.</param>
        public WildcardMatcher(string pattern, bool ignoreCase = true)
        {
            if (pattern is null) throw new ArgumentNullException(nameof(pattern));
            var options = RegexOptions.Singleline | RegexOptions.CultureInvariant;
            if (ignoreCase) options |= RegexOptions.IgnoreCase;
            _regex = new Regex("^(?:" + Translate(pattern) + ")$", options);
        }

        /// <summary>Returns true when <paramref name="input"/> matches the
        /// whole wildcard pattern.</summary>
        public bool IsMatch(string input)
        {
            if (input is null) throw new ArgumentNullException(nameof(input));
            return _regex.IsMatch(input);
        }

        private static string Translate(string pattern)
        {
            var sb = new StringBuilder(pattern.Length * 2);
            int i = 0;
            while (i < pattern.Length)
            {
                char c = pattern[i];
                switch (c)
                {
                    case '*': sb.Append(".*"); i++; break;
                    case '?': sb.Append('.'); i++; break;
                    case Escape: i = AppendEscaped(pattern, i, sb); break;
                    case '[': i = AppendCharClass(pattern, i, sb); break;
                    default: sb.Append(Regex.Escape(c.ToString())); i++; break;
                }
            }
            return sb.ToString();
        }

        private static int AppendEscaped(string pattern, int i, StringBuilder sb)
        {
            // A trailing backtick is treated as a literal backtick.
            if (i + 1 >= pattern.Length)
            {
                sb.Append(Regex.Escape(Escape.ToString()));
                return i + 1;
            }
            sb.Append(Regex.Escape(pattern[i + 1].ToString()));
            return i + 2;
        }

        private static int AppendCharClass(string pattern, int i, StringBuilder sb)
        {
            int close = pattern.IndexOf(']', i + 1);
            // Unterminated '[' is a literal '['.
            if (close < 0)
            {
                sb.Append(Regex.Escape("["));
                return i + 1;
            }
            sb.Append('[');
            for (int j = i + 1; j < close; j++)
            {
                char c = pattern[j];
                if (c == '\\' || c == '^' || c == ']') sb.Append('\\');
                sb.Append(c);
            }
            sb.Append(']');
            return close + 1;
        }
    }
}