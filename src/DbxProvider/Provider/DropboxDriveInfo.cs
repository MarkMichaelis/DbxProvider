using System.Management.Automation;
using Dropbox.Api;
using DbxProvider.Services;

namespace DbxProvider.Provider
{
    /// <summary>Custom PSDriveInfo that holds the Dropbox client connection.</summary>
    public class DropboxDriveInfo : PSDriveInfo
    {
        public DropboxServiceClient Service { get; }
        public DropboxClient Client { get; }

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
    }

    /// <summary>Dynamic parameters for New-PSDrive.</summary>
    public class DropboxDriveParameters
    {
        [Parameter(Mandatory = true)]
        public string AccessToken { get; set; } = string.Empty;
    }
}