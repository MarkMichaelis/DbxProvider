BeforeDiscovery {
    Import-Module (Join-Path $PSScriptRoot '..\Helpers\TestEnvironment.psm1') -Force
    $script:HasCredentials = [bool]((Get-DbxTestSecrets).RefreshToken) -and [bool]((Get-DbxTestSecrets).AppKey)
}

BeforeAll {
    Import-Module Pester
    Import-Module (Join-Path $PSScriptRoot '..\Helpers\TestEnvironment.psm1') -Force
    $script:Secrets = Get-DbxTestSecrets
    if (-not $script:Secrets.RefreshToken) { return }
    Import-Module (Get-DbxProviderModulePath) -Force -DisableNameChecking -Global
    Connect-DbxTestDrive

    # Persistent fixture folder seeded by build/Seed-DbxTestFixtures.ps1.
    # Lives outside /DbxProviderTests because the xUnit DropboxFixture
    # deletes /DbxProviderTests on init for ephemeral tests.
    $script:FixtureFolder = '/DbxProviderFixtures'
}

AfterAll {
    Disconnect-DbxTestDrive
}

Describe 'Export-DropboxFile' -Skip:(-not $HasCredentials) {

    It 'exports a seeded cloud-document fixture' {
        $svc = (Get-PSDrive 'DbxTest').Service
        $items = $null
        try {
            $items = $svc.ListFolderAsync($script:FixtureFolder, $false, $false, [System.Threading.CancellationToken]::None).GetAwaiter().GetResult()
        }
        catch {
            Set-ItResult -Skipped -Because "Fixture folder $script:FixtureFolder not found. Run build/Seed-DbxTestFixtures.ps1."
            return
        }

        $exportable = $items | Where-Object { -not $_.IsFolder -and -not $_.IsDownloadable } | Select-Object -First 1
        if (-not $exportable) {
            Set-ItResult -Skipped -Because "No exportable cloud-document in $script:FixtureFolder. Drop a .gdoc/.gsheet/.gslides via Google Drive sync or 'New Google Docs' from the Dropbox web UI, or run build/Seed-DbxTestFixtures.ps1 on a Paper-migrated account."
            return
        }

        $bytes = Export-DropboxFile -Path $exportable.Path -DriveName 'DbxTest' -ErrorAction Stop
        $bytes | Should -Not -BeNullOrEmpty
        $bytes.Length | Should -BeGreaterThan 0
    }
}

