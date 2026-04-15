# DbxProvider - PowerShell Dropbox Provider

A comprehensive PowerShell module that exposes the full Dropbox API as a PowerShell provider and cmdlet set. Navigate your Dropbox like a file system and manage sharing, revisions, tags, locks, and more.

## Requirements

- PowerShell 7.4+
- .NET 8.0 SDK (for building)
- A Dropbox account and access token or app key

## Building

```powershell
dotnet build src\DbxProvider\DbxProvider.csproj -c Release
```

## Installation

```powershell
# After building, import the module:
Import-Module .\src\DbxProvider\bin\Release\net8.0\DbxProvider.dll

# Or copy the output to a PowerShell module directory:
$modPath = "$env:USERPROFILE\Documents\PowerShell\Modules\DbxProvider\1.0.0"
Copy-Item .\src\DbxProvider\bin\Release\net8.0\* $modPath -Recurse
Import-Module DbxProvider
```

## Quick Start

```powershell
# Connect with an access token
Connect-Dropbox -AccessToken "your-access-token"

# Or use OAuth2 flow (opens browser)
Connect-Dropbox -AppKey "your-app-key" -DriveName Dbx

# Navigate Dropbox like a file system
cd Dbx:\
dir                            # Get-ChildItem  - list folder
dir -Recurse                   # Recursive listing
cd Documents                   # Set-Location   - navigate
cat readme.txt                 # Get-Content    - read file
"Hello" | Set-Content test.txt # Set-Content    - write file

# File operations
mkdir "New Folder"             # New-Item -ItemType Directory
copy file.txt backup.txt       # Copy-Item
move old.txt new.txt           # Move-Item / Rename-Item
del unwanted.txt               # Remove-Item
del junk.txt -Force            # Permanently delete

# Check existence
Test-Path Documents\report.docx
```

## Provider Operations (Standard Cmdlets)

| PowerShell Cmdlet     | Dropbox API               | Description                  |
|-----------------------|---------------------------|------------------------------|
| `Get-ChildItem`       | list_folder               | List folder contents         |
| `Get-ChildItem -Rec`  | list_folder (recursive)   | List all items recursively   |
| `Get-Item`            | get_metadata              | Get file/folder metadata     |
| `Test-Path`           | get_metadata              | Check if item exists         |
| `New-Item -Type Dir`  | create_folder_v2          | Create a folder              |
| `New-Item`            | upload                    | Create a file with content   |
| `Remove-Item`         | delete_v2                 | Delete file/folder           |
| `Remove-Item -Force`  | permanently_delete        | Permanently delete           |
| `Copy-Item`           | copy_v2                   | Copy file/folder             |
| `Move-Item`           | move_v2                   | Move file/folder             |
| `Rename-Item`         | move_v2                   | Rename file/folder           |
| `Get-Content`         | download                  | Download/read file content   |
| `Set-Content`         | upload                    | Upload/write file content    |
| `Clear-Content`       | upload (empty)            | Clear file content           |
| `Get-ItemProperty`    | get_metadata              | Get detailed metadata        |

## Custom Cmdlets

### Authentication
```powershell
# Token-based auth
Connect-Dropbox -AccessToken "sl.xxxxx"

# OAuth2 with PKCE (interactive, opens browser)
Connect-Dropbox -AppKey "your-app-key"

# Disconnect
Disconnect-Dropbox
```

### Search
```powershell
Search-Dropbox "quarterly report"
Search-Dropbox "budget" -Path "/Finance" -MaxResults 50
```

### File Transfer (Large File Support)
```powershell
# Upload local file (auto-handles files >150MB via upload sessions)
Invoke-DropboxUpload -Source C:\local\bigfile.zip -DropboxPath /Backups/bigfile.zip

# Download to local
Invoke-DropboxDownload -Path /Documents/report.pdf -Destination C:\local\report.pdf
```

### Revisions
```powershell
# List file revisions
Get-DropboxRevision /Documents/report.docx

# Restore to previous revision
Restore-DropboxRevision -Path /Documents/report.docx -Rev "015f11a4362"
```

### Shared Links
```powershell
# Create shared link
New-DropboxSharedLink /Documents/report.pdf -Visibility public

# List shared links
Get-DropboxSharedLink
Get-DropboxSharedLink /Documents/report.pdf

# Get metadata for a shared link URL
Get-DropboxSharedLink -Url "https://www.dropbox.com/s/xxxxx/file.pdf"

# Revoke a shared link
Remove-DropboxSharedLink -Url "https://www.dropbox.com/s/xxxxx/file.pdf"
```

### Folder Sharing
```powershell
# Share a folder
Add-DropboxSharedFolder /Projects/TeamProject

# List shared folders
Get-DropboxSharedFolder

# Unshare
Remove-DropboxSharedFolder -SharedFolderId "84528192421"
```

### Members
```powershell
# Add member to shared folder
Add-DropboxMember -SharedFolderId "84528192421" -Email "colleague@example.com" -AccessLevel editor

# Add member to file
Add-DropboxMember -FilePath /Documents/report.pdf -Email "reviewer@example.com" -AccessLevel viewer

# List members
Get-DropboxMember -SharedFolderId "84528192421"
Get-DropboxMember -FilePath /Documents/report.pdf

# Remove member
Remove-DropboxMember -SharedFolderId "84528192421" -Email "former@example.com"
```

### Tags
```powershell
# Add tag
Add-DropboxTag /Documents/report.pdf -Tag "quarterly"

# Get tags
Get-DropboxTag /Documents/report.pdf

# Remove tag
Remove-DropboxTag /Documents/report.pdf -Tag "quarterly"
```

### File Locks
```powershell
# Lock files
Lock-DropboxFile /Documents/report.docx

# Check lock status
Get-DropboxFileLock /Documents/report.docx

# Unlock
Unlock-DropboxFile /Documents/report.docx
```

### Temporary Links & URL Saving
```powershell
# Get a temporary direct download link (4 hours)
Get-DropboxTemporaryLink /Documents/report.pdf

# Save a URL to Dropbox (Dropbox downloads it)
Save-DropboxUrl -DropboxPath /Downloads/file.zip -Url "https://example.com/file.zip"
```

### Previews & Thumbnails
```powershell
# Get file preview (PDF)
Get-DropboxPreview /Documents/report.docx -OutFile C:\temp\preview.pdf

# Get thumbnail
Get-DropboxThumbnail /Photos/vacation.jpg -Size w256h256 -Format png -OutFile C:\temp\thumb.png
```

### Paper Documents
```powershell
# Create a Paper doc
New-DropboxPaper /Documents/notes.paper -Content "# My Notes`nSome content" -ImportFormat markdown

# Update a Paper doc
Set-DropboxPaper /Documents/notes.paper -Content "Updated content" -UpdatePolicy append
```

### Export
```powershell
# Export a cloud doc (Google Docs, etc.) to a downloadable format
Export-DropboxFile /Documents/gdoc.gdoc -OutFile C:\temp\exported.docx
```

### Batch Operations
```powershell
# Batch copy
Copy-DropboxItemBatch -FromPath @("/a/1.txt","/a/2.txt") -ToPath @("/b/1.txt","/b/2.txt")

# Batch move
Move-DropboxItemBatch -FromPath @("/a/1.txt","/a/2.txt") -ToPath @("/b/1.txt","/b/2.txt")

# Batch delete
Remove-DropboxItemBatch -Path @("/old/file1.txt", "/old/file2.txt")
```

### Account Information
```powershell
# Get current account info
Get-DropboxAccount

# Get another user account by ID
Get-DropboxAccount -AccountId "dbid:xxxxx"

# Check space usage
Get-DropboxSpaceUsage
```

## API Coverage

This module covers the complete Dropbox API v2:

| API Namespace | Endpoints Covered |
|---------------|-------------------|
| **Files**     | list_folder, get_metadata, download, upload, upload_session/*, copy_v2, move_v2, delete_v2, permanently_delete, create_folder_v2, search_v2, list_revisions, restore, get_preview, get_thumbnail_v2, get_temporary_link, save_url, copy_batch_v2, move_batch_v2, delete_batch, export, paper/create, paper/update, tags/add, tags/remove, tags/get, lock_file_batch, unlock_file_batch, get_file_lock_batch |
| **Sharing**   | create_shared_link_with_settings, list_shared_links, revoke_shared_link, get_shared_link_metadata, share_folder, unshare_folder, list_folders, get_folder_metadata, add_folder_member, remove_folder_member, list_folder_members, add_file_member, remove_file_member_v2, list_file_members |
| **Users**     | get_current_account, get_account, get_space_usage |

## Pipeline Support

Many cmdlets support pipeline input for efficient batch processing:

```powershell
# Search and download all matches
Search-Dropbox "report" | ForEach-Object {
    Invoke-DropboxDownload -Path $_.Item.Path -Destination "C:\reports\$($_.Item.Name)"
}

# Get revisions for multiple files
Get-ChildItem Dbx:\Documents\*.docx | Get-DropboxRevision

# Tag all files in a folder
Get-ChildItem Dbx:\ProjectX | ForEach-Object { Add-DropboxTag -Path $_.Path -Tag "projectx" }
```

## License

MIT