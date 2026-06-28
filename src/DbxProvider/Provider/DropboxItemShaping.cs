using System;
using System.Management.Automation;
using IntelliTect.Dropbox;

namespace DbxProvider.Provider
{
    /// <summary>
    /// Shared shaping for Dropbox items emitted to the PowerShell pipeline by both
    /// the <see cref="DropboxProvider"/> and the <see cref="DbxProvider.Cmdlets.DropboxCmdletBase"/>
    /// cmdlets. Centralizing this prevents the provider and cmdlet output from
    /// drifting apart: every emitted item carries a drive-qualified
    /// <c>Path</c> (e.g. <c>Dbx:\Folder\file</c>) so it pipes straight into
    /// provider-aware cmdlets such as <c>Remove-Item</c> from any location, while
    /// the raw Dropbox API path (<c>/Folder/file</c>) is preserved on the
    /// <c>DropboxPath</c> note property.
    /// </summary>
    internal static class DropboxItemShaping
    {
        /// <summary>
        /// Wraps <paramref name="item"/> in a <see cref="PSObject"/> whose adapted
        /// <c>Path</c> member is shadowed with the drive-qualified provider path for
        /// <paramref name="driveName"/> and whose raw API path is preserved on the
        /// <c>DropboxPath</c> note property. A <see cref="PSNoteProperty"/> with the
        /// same name takes precedence over the adapted member for both member access
        /// and pipeline binding, so <c>Remove-Item -Path</c> routes back through the
        /// Dropbox provider rather than the current filesystem location.
        /// </summary>
        public static PSObject ToDriveQualifiedPSObject(DropboxItem item, string driveName)
        {
            ArgumentNullException.ThrowIfNull(item);

            var pso = PSObject.AsPSObject(item);
            pso.Properties.Add(new PSNoteProperty("DropboxPath", item.Path));
            pso.Properties.Add(new PSNoteProperty("Path", ToDriveQualifiedPath(item.Path, driveName)));
            return pso;
        }

        /// <summary>
        /// Converts a Dropbox API path (<c>/Folder/file</c>) to a drive-qualified
        /// provider path (<c>Dbx:\Folder\file</c>) for <paramref name="driveName"/>.
        /// </summary>
        public static string ToDriveQualifiedPath(string? apiPath, string driveName) =>
            driveName + ":" + (apiPath ?? string.Empty).Replace('/', '\\');
    }
}
