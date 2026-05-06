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

    It 'connects with -AccessToken (token mode) when refresh-token-derived access token supplied' {
        $body = @{
            grant_type    = 'refresh_token'
            refresh_token = $Secrets.RefreshToken
            client_id     = $Secrets.AppKey
            client_secret = $Secrets.AppSecret
        }
        $response = Invoke-RestMethod -Method Post `
            -Uri 'https://api.dropboxapi.com/oauth2/token' `
            -Body $body `
            -ContentType 'application/x-www-form-urlencoded'

        $response.access_token | Should -Not -BeNullOrEmpty

        Connect-Dropbox -AccessToken $response.access_token -DriveName 'DbxTestConnect' | Out-Null

        $drive = Get-PSDrive -Name 'DbxTestConnect' -ErrorAction Stop
        $drive | Should -Not -BeNullOrEmpty
        $drive.Provider.Name | Should -Be 'Dropbox'
    }
}

