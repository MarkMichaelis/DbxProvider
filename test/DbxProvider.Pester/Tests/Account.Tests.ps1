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
    Connect-DbxTestDrive
}

AfterAll {
    if ($script:Secrets -and $script:Secrets.RefreshToken) { Disconnect-DbxTestDrive }
}

Describe 'Dropbox Account cmdlets' -Skip:(-not $HasCredentials) {

    It 'Get-DropboxAccount returns the current account' {
        $account = Get-DropboxAccount -DriveName 'DbxTest'
        $account | Should -Not -BeNullOrEmpty
        $account.Email | Should -Not -BeNullOrEmpty
    }

    It 'Get-DropboxSpaceUsage returns usage info' {
        $usage = Get-DropboxSpaceUsage -DriveName 'DbxTest'
        $usage | Should -Not -BeNullOrEmpty
    }
}

