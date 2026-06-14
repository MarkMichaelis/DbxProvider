using System;
using System.Collections.ObjectModel;

namespace IntelliTect.Dropbox
{
    /// <summary>Represents a file or folder in Dropbox.</summary>
    public class DropboxItem
    {
        public string Name { get; set; } = string.Empty;
        public string Path { get; set; } = string.Empty;
        public string Id { get; set; } = string.Empty;
        public bool IsFolder { get; set; }
        public ulong Length { get; set; }
        public DateTime? ServerModified { get; set; }
        public DateTime? ClientModified { get; set; }
        public string Rev { get; set; } = string.Empty;
        public string ContentHash { get; set; } = string.Empty;
        public bool IsDeleted { get; set; }
        public string SharedFolderId { get; set; } = string.Empty;
        public string ParentSharedFolderId { get; set; } = string.Empty;
        public bool HasExplicitSharedMembers { get; set; }
        public string MediaInfoTag { get; set; } = string.Empty;
        public string SymlinkTarget { get; set; } = string.Empty;
        public bool IsDownloadable { get; set; } = true;

        public string ItemType => IsFolder ? "Folder" : "File";
        public string DisplaySize => IsFolder ? "" : FormatSize(Length);

        /// <summary>FileSystem-parity alias for <see cref="Path"/>.</summary>
        public string FullName => Path;

        /// <summary>FileSystem-parity alias for <see cref="ServerModified"/>.</summary>
        public DateTime? LastWriteTime => ServerModified;

        /// <summary>FileSystem-parity: file extension including the leading dot, or empty string.</summary>
        public string Extension
        {
            get
            {
                if (IsFolder || string.IsNullOrEmpty(Name)) return string.Empty;
                var dot = Name.LastIndexOf('.');
                return dot > 0 ? Name.Substring(dot) : string.Empty;
            }
        }

        /// <summary>FileSystem-parity: file name without extension.</summary>
        public string BaseName
        {
            get
            {
                if (string.IsNullOrEmpty(Name)) return string.Empty;
                if (IsFolder) return Name;
                var dot = Name.LastIndexOf('.');
                return dot > 0 ? Name.Substring(0, dot) : Name;
            }
        }

        /// <summary>
        /// Fixed-position status flags rendered as a 6-character mask (FileSystem
        /// <c>Mode</c> parity). Position 1: <c>d</c> folder; 2: <c>s</c> shared;
        /// 3: <c>l</c> symlink; 4: <c>c</c> cloud-only (not downloadable);
        /// 5: <c>z</c> zero-byte file; 6: <c>x</c> conflicted copy. A dash
        /// (<c>-</c>) marks an unset flag. Example: a zero-byte conflicted-copy
        /// file renders as <c>----zx</c>.
        /// </summary>
        public string Mode => new string(new[]
        {
            IsFolder ? 'd' : '-',
            IsShared ? 's' : '-',
            string.IsNullOrEmpty(SymlinkTarget) ? '-' : 'l',
            IsDownloadable ? '-' : 'c',
            !IsFolder && Length == 0 ? 'z' : '-',
            IsConflictedCopy ? 'x' : '-',
        });

        /// <summary>True when this item is shared, or lives under a shared folder.</summary>
        public bool IsShared =>
            !string.IsNullOrEmpty(SharedFolderId) ||
            !string.IsNullOrEmpty(ParentSharedFolderId) ||
            HasExplicitSharedMembers;

        /// <summary>
        /// True when this is a file whose name contains the Dropbox conflict token
        /// (<c>conflicted copy</c>, case-insensitive) -- the same signal
        /// <c>Find-DropboxConflict</c> matches on.
        /// </summary>
        public bool IsConflictedCopy =>
            !IsFolder &&
            Name.IndexOf("conflicted copy", StringComparison.OrdinalIgnoreCase) >= 0;

        private static string FormatSize(ulong bytes)
        {
            string[] suffixes = { "B", "KB", "MB", "GB", "TB" };
            int i = 0;
            double size = bytes;
            while (size >= 1024 && i < suffixes.Length - 1) { size /= 1024; i++; }
            return $"{size:0.##} {suffixes[i]}";
        }

        public override string ToString() => $"{(IsFolder ? "[D]" : "[F]")} {Name}";
    }

    public class DropboxRevision
    {
        public string Name { get; set; } = string.Empty;
        public string Path { get; set; } = string.Empty;
        public string Rev { get; set; } = string.Empty;
        public ulong Length { get; set; }
        public DateTime? ServerModified { get; set; }
        public DateTime? ClientModified { get; set; }
        public string ContentHash { get; set; } = string.Empty;
        public bool IsDeleted { get; set; }
    }

    public class DropboxSharedLink
    {
        public string Url { get; set; } = string.Empty;
        public string Path { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string Id { get; set; } = string.Empty;
        public DateTime? Expires { get; set; }
        public string Visibility { get; set; } = string.Empty;
        public string LinkAccessLevel { get; set; } = string.Empty;
    }

    public class DropboxSharedFolder
    {
        public string SharedFolderId { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string PathLower { get; set; } = string.Empty;
        public string AccessType { get; set; } = string.Empty;
        public bool IsInsideTeamFolder { get; set; }
        public bool IsTeamFolder { get; set; }
        public string Policy { get; set; } = string.Empty;
        public Collection<DropboxMember> Members { get; set; } = new();
    }

    public class DropboxMember
    {
        public string AccountId { get; set; } = string.Empty;
        public string Email { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public string AccessLevel { get; set; } = string.Empty;
        public bool IsInherited { get; set; }
    }

    public class DropboxAccount
    {
        public string AccountId { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public string Email { get; set; } = string.Empty;
        public bool EmailVerified { get; set; }
        public string ProfilePhotoUrl { get; set; } = string.Empty;
        public string Country { get; set; } = string.Empty;
        public string Locale { get; set; } = string.Empty;
        public string TeamName { get; set; } = string.Empty;
        public string AccountType { get; set; } = string.Empty;
        public string ReferralLink { get; set; } = string.Empty;
        public bool IsPaired { get; set; }
    }

    public class DropboxSpaceUsage
    {
        public ulong Used { get; set; }
        public ulong Allocated { get; set; }
        public string AllocationLabel { get; set; } = string.Empty;

        public string UsedDisplay => FormatSize(Used);
        public string AllocatedDisplay => FormatSize(Allocated);
        public double PercentUsed => Allocated > 0 ? Math.Round((double)Used / Allocated * 100, 2) : 0;

        private static string FormatSize(ulong bytes)
        {
            string[] suffixes = { "B", "KB", "MB", "GB", "TB" };
            int i = 0;
            double size = bytes;
            while (size >= 1024 && i < suffixes.Length - 1) { size /= 1024; i++; }
            return $"{size:0.##} {suffixes[i]}";
        }
    }

    public class DropboxSearchResult
    {
        public string MatchType { get; set; } = string.Empty;
        public DropboxItem? Item { get; set; }
        public string HighlightedTitle { get; set; } = string.Empty;
    }

    public class DropboxTag
    {
        public string Path { get; set; } = string.Empty;
        public string TagText { get; set; } = string.Empty;
    }
}