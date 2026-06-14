namespace IntelliTect.Dropbox;

/// <summary>
/// Describes a single entry that the Dropbox <c>delete_batch</c> operation could
/// not delete -- for example, a path that no longer exists (a re-deleted
/// "conflicted copy" file). Returned by
/// <see cref="DropboxServiceClient.DeleteBatchAsync"/> so callers can surface
/// per-item failures instead of silently treating them as successes.
/// </summary>
public sealed class DropboxBatchDeleteError
{
    /// <summary>Creates a failure record for a single batch-delete entry.</summary>
    /// <param name="path">The path (as submitted) that could not be deleted.</param>
    /// <param name="reason">A short, human-readable description of the failure.</param>
    public DropboxBatchDeleteError(string path, string reason)
    {
        Path = path;
        Reason = reason;
    }

    /// <summary>The path (as submitted) that could not be deleted.</summary>
    public string Path { get; }

    /// <summary>A short, human-readable description of the failure.</summary>
    public string Reason { get; }
}