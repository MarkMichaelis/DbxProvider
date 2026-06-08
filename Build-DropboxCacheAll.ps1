#requires -Version 7.4
<#
.SYNOPSIS
    Build the DbxProvider metadata cache for an entire Dropbox account,
    one top-level folder at a time, with visible progress and full resumability.

.DESCRIPTION
    Build-DropboxCache over the whole account root issues a single recursive
    listing that, on a very large account (millions of files), can take a long
    time to return its first page and shows no progress in the meantime. This
    script instead builds each top-level folder separately: small folders finish
    in seconds (so progress is visible immediately), and each folder's pages are
    persisted to the cache database as they arrive.

    Build-DropboxCache is itself resumable -- it records a per-folder cursor in
    the cache's build_progress table -- so re-running this script after an
    interruption (Ctrl+C, crash, reboot) continues where it left off instead of
    restarting. Huge backup archives (Synology ActiveBackup* and .sync) are
    deferred to the end with -BigFoldersLast so the rest of the account is cached
    first.

    Only ONE process may write the cache database at a time (SQLite single
    writer). Do not run two cache builds concurrently.

.PARAMETER DriveName
    Dropbox PSDrive name. Defaults to 'Dbx'. The drive is mounted via
    Connect-Dropbox if it is not already.

.PARAMETER ModulePath
    Path to the DbxProvider module manifest to import. Defaults to the Debug
    build next to this script.

.PARAMETER IncludeRevisions
    Also cache each file's revision history (much slower; off by default).

.PARAMETER BigFoldersLast
    Names of large top-level folders to build last. Defaults to the Synology
    backup archives and the .sync control folder.

.PARAMETER LogPath
    Append a timestamped progress line per folder to this file. Defaults to a
    local (non-OneDrive) path so the log is never locked by a sync client.

.EXAMPLE
    ./Build-DropboxCacheAll.ps1
    Build the whole account, normal folders first, big archives last. Re-run to
    resume after any interruption.

.EXAMPLE
    ./Build-DropboxCacheAll.ps1 -IncludeRevisions
    Also cache revision history for every file.

.NOTES
    After the cache is built, a complete zero-byte "conflicted copy" inventory
    can be extracted from the cache database with no further Dropbox API calls,
    because every folder's children (including each file's Length) are stored in
    the cache 'entries' table as a camelCase JSON array.
#>
[CmdletBinding()]
param(
    [string]$DriveName = 'Dbx',
    [string]$ModulePath = (Join-Path $PSScriptRoot 'src\DbxProvider\bin\Debug\net8.0\DbxProvider.psd1'),
    [switch]$IncludeRevisions,
    [string[]]$BigFoldersLast = @('.sync', 'ActiveBackupForGSuite', 'ActiveBackupForGoogleWorkspace', 'ActiveBackupForOffice365'),
    [string]$LogPath = (Join-Path $env:USERPROFILE 'Build-DropboxCacheAll.log')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Write-Log {
    param([string]$Message)
    $line = '{0}  {1}' -f (Get-Date -Format 'HH:mm:ss'), $Message
    Write-Host $line
    try { $line | Add-Content -LiteralPath $LogPath } catch { }
}

if (-not (Get-Module DbxProvider)) {
    if (-not (Test-Path -LiteralPath $ModulePath)) {
        throw "DbxProvider module not found at '$ModulePath'. Build it or pass -ModulePath."
    }
    Import-Module $ModulePath -ErrorAction Stop
}

if (-not (Get-PSDrive -Name $DriveName -ErrorAction SilentlyContinue)) {
    Write-Host "Connecting to Dropbox (drive '$DriveName' not mounted)..." -ForegroundColor Cyan
    Connect-Dropbox | Out-Null
}

"=== Build-DropboxCacheAll started $(Get-Date -Format o) ===" | Add-Content -LiteralPath $LogPath

# Order: normal folders first (sorted) so progress is visible quickly, then the
# big archives last.
$all = @(Get-ChildItem "${DriveName}:\" | Where-Object IsFolder | Select-Object -ExpandProperty Name)
$deferred = @($all | Where-Object { $_ -in $BigFoldersLast })
$ordered = @($all | Where-Object { $_ -notin $BigFoldersLast } | Sort-Object) + $deferred
Write-Log ("Building {0} top-level folders; {1} large archive(s) deferred to the end." -f $ordered.Count, $deferred.Count)

$grandItems = 0
$grandFolders = 0
$i = 0
foreach ($name in $ordered) {
    $i++
    $path = '/' + $name
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    try {
        $r = Build-DropboxCache -Path $path -DriveName $DriveName -IncludeRevisions:$IncludeRevisions -ErrorAction Stop
        $sw.Stop()
        $grandItems += $r.ItemsFound
        $grandFolders += $r.FoldersCached
        Write-Log ('[{0}/{1}] OK   folders={2,-7} items={3,-8} ({4}s)  {5}' -f `
                $i, $ordered.Count, $r.FoldersCached, $r.ItemsFound, [int]$sw.Elapsed.TotalSeconds, $path)
    }
    catch {
        $sw.Stop()
        Write-Log ('[{0}/{1}] FAIL ({2}s)  {3}  -- {4}' -f `
                $i, $ordered.Count, [int]$sw.Elapsed.TotalSeconds, $path, ($_.Exception.Message -replace '\s+', ' '))
    }
}

Write-Log ("=== FINISHED: {0} folders cached, {1} items across {2} top-level trees ===" -f `
        $grandFolders, $grandItems, $ordered.Count)
Write-Host "Cache database: $(Get-DropboxCacheDatabasePath)" -ForegroundColor Green
