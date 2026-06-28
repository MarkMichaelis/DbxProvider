using System.Collections.Generic;

namespace IntelliTect.Dropbox;

/// <summary>
/// Describes a single entry that a Dropbox batch copy or move
/// (<c>copy_batch</c> / <c>move_batch</c>) could not relocate -- for example a
/// source path that no longer exists or a destination conflict. Returned as part
/// of <see cref="DropboxBatchRelocationResult"/> so callers can surface per-item
/// failures instead of silently dropping them.
/// </summary>
public sealed class DropboxBatchRelocationError
{
    /// <summary>Creates a failure record for a single relocation entry.</summary>
    /// <param name="reason">A short, human-readable description of the failure.</param>
    public DropboxBatchRelocationError(string reason)
    {
        Reason = reason;
    }

    /// <summary>A short, human-readable description of the failure.</summary>
    public string Reason { get; }
}

/// <summary>
/// The outcome of a Dropbox batch copy or move: the items that were successfully
/// relocated and the per-entry failures that were not. Replaces returning only
/// the successful items, which made a partial failure indistinguishable from
/// missing output.
/// </summary>
public sealed class DropboxBatchRelocationResult
{
    /// <summary>Creates a relocation result from the successes and failures.</summary>
    /// <param name="items">The items that were successfully relocated.</param>
    /// <param name="failures">The per-entry failures that occurred.</param>
    public DropboxBatchRelocationResult(
        IReadOnlyList<DropboxItem> items,
        IReadOnlyList<DropboxBatchRelocationError> failures)
    {
        Items = items;
        Failures = failures;
    }

    /// <summary>The items that were successfully relocated.</summary>
    public IReadOnlyList<DropboxItem> Items { get; }

    /// <summary>The per-entry failures that occurred (empty when all succeeded).</summary>
    public IReadOnlyList<DropboxBatchRelocationError> Failures { get; }
}
