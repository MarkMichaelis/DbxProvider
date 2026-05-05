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
    Initialize-DbxTestRoot
    $script:Folder = New-DbxTestFolder -TestName 'Navigation'
}

AfterAll {
    if ($script:Folder) { Remove-DbxTestFolder -Path $script:Folder.ProviderPath }
    Disconnect-DbxTestDrive
}

Describe 'Provider Navigation' -Skip:(-not $HasCredentials) {

    It 'Set-Location to drive root' {
        Set-Location 'DbxTest:\'
        (Get-Location).Provider.Name | Should -Be 'Dropbox'
    }

    It 'Set-Location into a test folder' {
        Set-Location -LiteralPath $Folder.ProviderPath
        (Get-Location).Path | Should -Match ([Regex]::Escape($Folder.Name))
    }

    It 'Test-Path returns true for an existing folder' {
        Test-Path -LiteralPath $Folder.ProviderPath | Should -BeTrue
    }

    It 'Test-Path returns false for a non-existent folder' {
        Test-Path -LiteralPath ($Folder.ProviderPath + '\does-not-exist-xyz') | Should -BeFalse
    }

    It 'Push-Location and Pop-Location round-trip' {
        Set-Location 'DbxTest:\'
        $before = (Get-Location).Path
        Push-Location -LiteralPath $Folder.ProviderPath
        (Get-Location).Path | Should -Not -Be $before
        Pop-Location
        (Get-Location).Path | Should -Be $before
    }
}

