using System;
using System.IO;

namespace IntelliTect.Dropbox
{
    /// <summary>
    /// Resolves reads against a local Dropbox mirror, returning a stream over a
    /// local copy only when it provably matches the Dropbox master. All gating
    /// is conservative: any uncertainty (missing file, size mismatch, hash
    /// mismatch, or a cloud-placeholder that is not materialized locally) yields
    /// <see langword="null"/> so the caller transparently falls back to an API
    /// download. Because equality is gated on the master's <c>content_hash</c>,
    /// extra or stale files in the mirror can never be served as if they were
    /// the master.
    /// </summary>
    public sealed class LocalMirrorResolver
    {
        // Cloud-provider placeholder attributes. A file carrying any of these is
        // an on-demand stub whose bytes are not present locally; reading it would
        // trigger a (slow) hydration download, defeating the optimization.
        private const FileAttributes RecallOnOpen = (FileAttributes)0x00040000;
        private const FileAttributes RecallOnDataAccess = (FileAttributes)0x00400000;

        private readonly LocalMirrorOptions _options;

        /// <summary>Creates a resolver bound to <paramref name="options"/>.</summary>
        /// <param name="options">Mirror configuration.</param>
        public LocalMirrorResolver(LocalMirrorOptions options)
        {
            _options = options ?? throw new ArgumentNullException(nameof(options));
        }

        /// <summary>The options this resolver was created with.</summary>
        public LocalMirrorOptions Options => _options;

        /// <summary>
        /// Maps a Dropbox path (for example <c>/Foo/Bar.txt</c>) to the
        /// corresponding local path under <see cref="LocalMirrorOptions.Root"/>,
        /// or <see langword="null"/> when no root is configured.
        /// </summary>
        /// <param name="dropboxPath">The Dropbox path to map.</param>
        public string? MapToLocalPath(string dropboxPath)
        {
            if (string.IsNullOrEmpty(_options.Root) || dropboxPath is null)
            {
                return null;
            }

            string relative = dropboxPath
                .Replace('/', Path.DirectorySeparatorChar)
                .TrimStart(Path.DirectorySeparatorChar);

            return relative.Length == 0
                ? _options.Root!
                : Path.Combine(_options.Root!, relative);
        }

        /// <summary>
        /// Attempts to open a verified local copy of <paramref name="dropboxPath"/>.
        /// Returns a readable stream when the local file is present, fully
        /// materialized, and matches <paramref name="master"/>; otherwise returns
        /// <see langword="null"/>.
        /// </summary>
        /// <param name="dropboxPath">The Dropbox path being read.</param>
        /// <param name="master">The authoritative Dropbox metadata for the file.</param>
        public Stream? TryOpenVerified(string dropboxPath, DropboxItem master)
        {
            if (master is null)
            {
                throw new ArgumentNullException(nameof(master));
            }

            if (!_options.Enabled || master.IsFolder)
            {
                return null;
            }

            string? localPath = MapToLocalPath(dropboxPath);
            if (localPath is null || !File.Exists(localPath))
            {
                return null;
            }

            var info = new FileInfo(localPath);
            if (IsCloudPlaceholder(info.Attributes))
            {
                return null;
            }

            if ((ulong)info.Length != master.Length)
            {
                return null;
            }

            if (_options.VerifyContentHash)
            {
                if (string.IsNullOrEmpty(master.ContentHash))
                {
                    return null;
                }

                string localHash = DropboxContentHasher.ComputeFileHash(localPath);
                if (!string.Equals(localHash, master.ContentHash, StringComparison.OrdinalIgnoreCase))
                {
                    return null;
                }
            }

            return File.OpenRead(localPath);
        }

        /// <summary>
        /// Returns <see langword="true"/> when <paramref name="attributes"/> mark a
        /// cloud-provider placeholder (on-demand / online-only) file whose content
        /// is not present locally and would have to be recalled over the network.
        /// </summary>
        /// <param name="attributes">The file's attributes.</param>
        public static bool IsCloudPlaceholder(FileAttributes attributes)
        {
            const FileAttributes mask =
                FileAttributes.Offline | RecallOnOpen | RecallOnDataAccess;
            return (attributes & mask) != 0;
        }
    }
}
