BeforeDiscovery {
    Import-Module (Join-Path $PSScriptRoot '..\Helpers\TestEnvironment.psm1') -Force
    $script:HasCredentials = [bool]((Get-DbxTestSecrets).RefreshToken) -and [bool]((Get-DbxTestSecrets).AppKey)
}

BeforeAll {
    Import-Module Pester
    Import-Module (Join-Path $PSScriptRoot '..\Helpers\TestEnvironment.psm1') -Force
    Import-Module (Join-Path $PSScriptRoot '..\Helpers\TestRoot.psm1') -Force
    $script:Secrets = Get-DbxTestSecrets
    if (-not $script:Secrets.RefreshToken) { return }
    Import-Module (Get-DbxProviderModulePath) -Force -DisableNameChecking -Global
}

AfterAll {
    if ($script:Secrets -and $script:Secrets.RefreshToken) {
        Disconnect-DbxTestDrive -DriveName 'DbxTestConnect' -ErrorAction SilentlyContinue
    }
}

Describe 'Connect-Dropbox' -Skip:(-not $HasCredentials) {

    AfterEach {
        if (Get-PSDrive -Name 'DbxTestConnect' -ErrorAction SilentlyContinue) {
            Disconnect-Dropbox -DriveName 'DbxTestConnect' -ErrorAction SilentlyContinue
        }
    }

    It 'connects with refresh token and creates a PSDrive' {
        Connect-Dropbox `
            -AppKey       $Secrets.AppKey `
            -AppSecret    $Secrets.AppSecret `
            -RefreshToken $Secrets.RefreshToken `
            -DriveName    'DbxTestConnect' `
            -NoSave | Out-Null

        $drive = Get-PSDrive -Name 'DbxTestConnect' -ErrorAction Stop
        $drive | Should -Not -BeNullOrEmpty
        $drive.Provider.Name | Should -Be 'Dropbox'
    }

    It 'Disconnect-Dropbox removes the drive' {
        Connect-Dropbox `
            -AppKey       $Secrets.AppKey `
            -AppSecret    $Secrets.AppSecret `
            -RefreshToken $Secrets.RefreshToken `
            -DriveName    'DbxTestConnect' `
            -NoSave | Out-Null

        Disconnect-Dropbox -DriveName 'DbxTestConnect'

        Get-PSDrive -Name 'DbxTestConnect' -ErrorAction SilentlyContinue | Should -BeNullOrEmpty
    }

    It 'connects with -AccessToken (token mode) when refresh-token-derived access token supplied' -Skip {
        # Skipped: requires a short-lived access token; refresh-token mode is the canonical
        # path for CI. Left here for future expansion.
    }
}

