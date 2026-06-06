@{
    RootModule = 'DbxProvider.dll'
    ModuleVersion = '1.0.0'
    GUID = 'b8e7c3a1-4d2f-4e5a-9c6b-7d8e9f0a1b2c'
    Author = 'DbxProvider Contributors'
    CompanyName = 'Community'
    Copyright = '(c) 2026 DbxProvider Contributors. All rights reserved.'
    Description = 'PowerShell provider and cmdlets for Dropbox API. Navigate your Dropbox as a drive, manage files, sharing, tags, revisions, and more.'
    PowerShellVersion = '7.4'
    DotNetFrameworkVersion = '8.0'
    FormatsToProcess = @('DbxProvider.Format.ps1xml')
    FunctionsToExport = @()
    CmdletsToExport = @(
        # Authentication
        'Connect-Dropbox',
        'Disconnect-Dropbox',
        # Credentials
        'Get-DropboxCredential',
        'Set-DropboxCredential',
        'Remove-DropboxCredential',
        # Cache
        'Get-DropboxCacheInfo',
        'Clear-DropboxCache',
        'Update-DropboxCache',
        'Build-DropboxCache',
        'Set-DropboxCacheOption',
        'Set-DropboxCacheDatabasePath',
        'Remove-DropboxCacheDatabasePath',
        'Get-DropboxCacheDatabasePath',
        # Search
        'Search-Dropbox',        # Revisions
        # Conflict scan
        'Find-DropboxConflict',        # Revisions
        'Get-DropboxRevision',
        'Restore-DropboxRevision',
        # Transfer
        'Invoke-DropboxDownload',
        'Invoke-DropboxUpload',
        # Shared Links
        'New-DropboxSharedLink',
        'Get-DropboxSharedLink',
        'Remove-DropboxSharedLink',
        # Sharing
        'Add-DropboxSharedFolder',
        'Remove-DropboxSharedFolder',
        'Get-DropboxSharedFolder',
        'Add-DropboxMember',
        'Remove-DropboxMember',
        'Get-DropboxMember',
        # Tags
        'Add-DropboxTag',
        'Remove-DropboxTag',
        'Get-DropboxTag',
        # Account
        'Get-DropboxAccount',
        'Get-DropboxSpaceUsage',
        # Links
        'Get-DropboxTemporaryLink',
        'Save-DropboxUrl',
        # Preview
        'Get-DropboxPreview',
        'Get-DropboxThumbnail',
        # Paper
        'New-DropboxPaper',
        'Set-DropboxPaper',
        # Export / Batch
        'Export-DropboxFile',
        'Copy-DropboxItemBatch',
        'Move-DropboxItemBatch',
        'Remove-DropboxItemBatch'
    )
    VariablesToExport = @()
    AliasesToExport = @()
    PrivateData = @{
        PSData = @{
            Tags = @('Dropbox', 'Provider', 'CloudStorage', 'FileSystem', 'PSProvider')
            LicenseUri = ''
            ProjectUri = ''
            ReleaseNotes = 'Initial release with full Dropbox API coverage via PSProvider and cmdlets.'
        }
    }
}