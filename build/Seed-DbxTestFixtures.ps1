<#
.SYNOPSIS
    One-shot seeder that populates persistent fixture documents in the Dropbox
    test account so that read-only tests (Export, etc.) have stable inputs.

.DESCRIPTION
    Some Dropbox APIs (notably /2/files/export) only operate on cloud-document
    types like Paper docs and Google Docs. These cannot be checked into source
    control - they live server-side. Running this script once per test account
    creates the required fixtures under:

        /DbxProviderTests/Fixtures/

    Fixtures are idempotent: re-running the script is safe and leaves existing
    files unchanged.

    Credentials are resolved by Get-DbxTestSecrets (env vars, dotnet
    user-secrets, or the shared CredentialStore) - the same chain the test
    suite uses.

.EXAMPLE
    pwsh -File build/Seed-DbxTestFixtures.ps1
#>
[CmdletBinding()]
param(
    [string]$FixtureRoot = '/DbxProviderFixtures'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot '..')
Import-Module (Join-Path $repoRoot 'test\DbxProvider.Pester\Helpers\TestEnvironment.psm1') -Force

$secrets = Get-DbxTestSecrets
if (-not $secrets.RefreshToken -or -not $secrets.AppKey) {
    throw 'Dropbox credentials not found. Set DBX_APP_KEY / DBX_APP_SECRET / DBX_REFRESH_TOKEN, or run Connect-Dropbox once.'
}

Import-Module (Get-DbxProviderModulePath) -Force -DisableNameChecking -Global

$driveName = 'DbxSeed'
if (Get-PSDrive -Name $driveName -ErrorAction SilentlyContinue) {
    Remove-PSDrive -Name $driveName -Force
}

$connectArgs = @{
    AppKey       = $secrets.AppKey
    RefreshToken = $secrets.RefreshToken
    DriveName    = $driveName
    NoSave       = $true
}
if ($secrets.AppSecret) { $connectArgs.AppSecret = $secrets.AppSecret }

Connect-Dropbox @connectArgs | Out-Null

try {
    # Ensure the fixture folder exists. Use the underlying service (not the
    # provider's New-Item) so the folder is created in the regular Files
    # namespace - the only namespace /2/files/list_folder + /2/files/export
    # operate against.
    $svc = (Get-PSDrive $driveName).Service
    try {
        $svc.CreateFolderAsync($FixtureRoot, [System.Threading.CancellationToken]::None).GetAwaiter().GetResult() | Out-Null
        Write-Host "Created fixture folder $FixtureRoot" -ForegroundColor Green
    }
    catch {
        if ($_.Exception.Message -match 'conflict|already_exists|path/conflict') {
            Write-Host "Fixture folder $FixtureRoot already exists." -ForegroundColor DarkGray
        }
        else { throw }
    }

    # Inventory current fixtures (anything non-downloadable counts as exportable).
    $existing = $svc.ListFolderAsync($FixtureRoot, $false, $false, [System.Threading.CancellationToken]::None).GetAwaiter().GetResult()
    $exportable = $existing | Where-Object { -not $_.IsFolder -and -not $_.IsDownloadable }
    if ($exportable) {
        Write-Host "Exportable fixture(s) already present:" -ForegroundColor Green
        foreach ($e in $exportable) { Write-Host "  $($e.Path)" -ForegroundColor DarkGray }
        return
    }

    # Try to seed via Paper. NOTE: on accounts where Paper has not been
    # migrated to the Files namespace (i.e. legacy 'Dropbox Paper' product),
    # a created .paper doc lives in a separate Paper namespace and is NOT
    # visible to /2/files/list_folder or /2/files/export. We detect that
    # case by listing the folder again post-create and falling back to a
    # printed instruction for manual seeding.
    $paperApiPath = "$FixtureRoot/Exportable.paper"
    $body = @"
# DbxProvider Export-Test Fixture

Permanent Paper document used by the DbxProvider test suite to exercise the
``/2/files/export`` API path. Do not delete.
"@
    try {
        New-DropboxPaper -Path $paperApiPath -Content $body -ImportFormat markdown -DriveName $driveName -ErrorAction Stop | Out-Null
        Write-Host "Called New-DropboxPaper for $paperApiPath" -ForegroundColor DarkGray
    }
    catch {
        $msg = $_.Exception.Message
        Write-Warning "New-DropboxPaper failed: $msg"
    }

    Start-Sleep -Seconds 3
    $verify = $svc.ListFolderAsync($FixtureRoot, $false, $false, [System.Threading.CancellationToken]::None).GetAwaiter().GetResult()
    if ($verify | Where-Object { -not $_.IsFolder -and -not $_.IsDownloadable }) {
        Write-Host "Seeded $paperApiPath and confirmed it is visible to /2/files/list_folder." -ForegroundColor Green
        return
    }

    Write-Warning @"
The Paper doc was created but is not visible via /2/files/list_folder, which
means this account stores Paper docs in a separate (non-migrated) namespace.
Cloud-export tests cannot reach it.

To enable the export tests on this account, drop a Google Docs file into
$FixtureRoot manually:
  - Easiest: open Dropbox in a browser, navigate to $FixtureRoot, and click
    'Create -> Google Docs / Sheets / Slides'.
  - Or: with Google Drive desktop sync, place a Google Docs shortcut (.gdoc)
    into the synced Dropbox folder under $FixtureRoot.

Either approach lands a non-downloadable cloud document in the Files
namespace, which the export tests will pick up automatically.
"@
}
finally {
    if (Get-PSDrive -Name $driveName -ErrorAction SilentlyContinue) {
        Remove-PSDrive -Name $driveName -Force -ErrorAction SilentlyContinue
    }
}
