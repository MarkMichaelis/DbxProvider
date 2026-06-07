using System;
using System.Management.Automation;
using Dropbox.Api;
using IntelliTect.Dropbox;

namespace DbxProvider.Provider
{
    /// <summary>Custom PSDriveInfo that holds the Dropbox client connection.</summary>
    public class DropboxDriveInfo : PSDriveInfo, IDisposable
    {
        public DropboxServiceClient Service { get; }
        public DropboxClient Client { get; }
        public MetadataCache? Cache { get; private set; }
        public string? AccountId { get; private set; }

        public DropboxDriveInfo(PSDriveInfo driveInfo, string accessToken) : base(driveInfo)
        {
            Client = new DropboxClient(accessToken);
            Service = new DropboxServiceClient(Client);
        }

        public DropboxDriveInfo(PSDriveInfo driveInfo, DropboxServiceClient service) : base(driveInfo)
        {
            Service = service;
            Client = null!;
        }

        /// <summary>
        /// Initialize (or re-initialize) the metadata cache for this drive,
        /// scoped to the given Dropbox account. The account email (when
        /// available) names the on-disk cache file; otherwise a hash of the
        /// account id is used. Hydrates from disk.
        /// </summary>
        public void InitializeCache(string accountId, string? email = null, CacheOptions? options = null)
        {
            AccountId = accountId;
            Cache?.Dispose();
            Cache = new MetadataCache(Service, accountId, email, options ?? CacheOptions.Default);
        }

        public void Dispose()
        {
            try { Cache?.Dispose(); } catch { }
            try { Service?.Dispose(); } catch { }
        }
    }

    /// <summary>Dynamic parameters for New-PSDrive.</summary>
    public class DropboxDriveParameters
    {
        [Parameter(Mandatory = true)]
        public string AccessToken { get; set; } = string.Empty;

        /// <summary>
        /// Optional local folder (or NAS share) mirroring this account. When set,
        /// verified-equal files are read locally instead of via the API. When
        /// omitted, the Dropbox desktop folder is auto-detected from info.json.
        /// </summary>
        [Parameter]
        public string? LocalMirrorRoot { get; set; }

        /// <summary>Disable the local-mirror read accelerator entirely.</summary>
        [Parameter]
        public SwitchParameter NoLocalMirror { get; set; }
    }
}
