using System;
using System.Collections.Generic;
using System.Text.Json;

namespace IntelliTect.Dropbox
{
    /// <summary>A single conflict file the finder matched.</summary>
    public sealed class ConflictMatch
    {
        /// <summary>Display path of the matched file.</summary>
        public string Path { get; set; } = string.Empty;

        /// <summary>Size of the matched file in bytes.</summary>
        public ulong Bytes { get; set; }
    }

    /// <summary>
    /// Read-only reader for the obsolete pre-cache conflict-scan sidecar
    /// (<c>*.state.json</c>). Conflict finding is now backed by the metadata
    /// cache, so this type exists solely to detect and migrate a sidecar written
    /// by an earlier version -- archiving it rather than erroring or silently
    /// discarding the user's saved matches. The legacy shape is PascalCase
    /// (AccountId, StartPath, Pattern, IncludeNonZero, Cursor, Matches).
    /// </summary>
    public sealed class LegacyConflictScanState
    {
        /// <summary>Account the saved cursor and matches belonged to.</summary>
        public string AccountId { get; set; } = string.Empty;

        /// <summary>Normalized start path the cursor was scoped to.</summary>
        public string StartPath { get; set; } = string.Empty;

        /// <summary>Conflict pattern the matches were computed with.</summary>
        public string Pattern { get; set; } = string.Empty;

        /// <summary>IncludeNonZero flag the matches were computed with.</summary>
        public bool IncludeNonZero { get; set; }

        /// <summary>Saved account-wide recursive cursor.</summary>
        public string Cursor { get; set; } = string.Empty;

        /// <summary>Saved matches, keyed by lowercased path.</summary>
        public Dictionary<string, ConflictMatch> Matches { get; set; } =
            new(StringComparer.Ordinal);

        private static readonly JsonSerializerOptions Options =
            new() { PropertyNameCaseInsensitive = true };

        /// <summary>Parses a legacy sidecar, returning <c>null</c> when the input
        /// is blank, not valid JSON, or missing the fields a real sidecar always
        /// carries (a non-empty <see cref="AccountId"/> and <see cref="Cursor"/>).
        /// The shape check keeps an unrelated JSON file pointed at via
        /// <c>-StatePath</c> (for example <c>{}</c>) from being mistaken for
        /// legacy state and archived.</summary>
        public static LegacyConflictScanState? FromJson(string? json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            try
            {
                var state = JsonSerializer.Deserialize<LegacyConflictScanState>(json!, Options);
                if (state == null
                    || string.IsNullOrEmpty(state.AccountId)
                    || string.IsNullOrEmpty(state.Cursor))
                {
                    return null;
                }
                return state;
            }
            catch (JsonException) { return null; }
        }
    }
}