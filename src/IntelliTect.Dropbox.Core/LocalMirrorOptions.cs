namespace IntelliTect.Dropbox
{
    /// <summary>
    /// Configures the optional local-mirror read accelerator. When a Dropbox
    /// account is also mirrored to a local folder or a NAS share, verified-equal
    /// files can be served from that mirror instead of being downloaded through
    /// the Dropbox API, avoiding network latency and API rate limits.
    /// </summary>
    public sealed class LocalMirrorOptions
    {
        /// <summary>
        /// When <see langword="false"/>, the resolver never serves local files
        /// and every read goes through the Dropbox API.
        /// </summary>
        public bool Enabled { get; set; } = true;

        /// <summary>
        /// Root folder of the local mirror (for example the Dropbox desktop
        /// folder discovered via <see cref="DropboxMirrorLocator"/>, or an
        /// explicit NAS share such as <c>\\nas\Data</c>). When
        /// <see langword="null"/> or empty the mirror is effectively disabled.
        /// </summary>
        public string? Root { get; set; }

        /// <summary>
        /// When <see langword="true"/> (the default) a local file is only served
        /// after its Dropbox <c>content_hash</c> is recomputed and confirmed to
        /// match the master, guaranteeing byte-for-byte equality even if the
        /// mirror is stale or holds an unrelated file of the same size. When
        /// <see langword="false"/> only the file size is checked, which is faster
        /// but a weaker guarantee.
        /// </summary>
        public bool VerifyContentHash { get; set; } = true;
    }
}
