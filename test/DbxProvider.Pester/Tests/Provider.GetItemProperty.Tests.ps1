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
    $script:Folder = New-DbxTestFolder -TestName 'GetItemProperty'

    $script:File = "$($Folder.ProviderPath)\props.txt"
    New-Item -Path $script:File -ItemType File -Value 'property test' -Force | Out-Null
}

AfterAll {
    if ($script:Folder) { Remove-DbxTestFolder -Path $script:Folder.ProviderPath }
    Disconnect-DbxTestDrive
}

Describe 'Provider Get-Item / Get-ItemProperty' -Skip:(-not $HasCredentials) {

    It 'Get-Item returns metadata for a file' {
        $item = Get-Item -LiteralPath $File
        $item | Should -Not -BeNullOrEmpty
        $item.Name | Should -Be 'props.txt'
    }

    It 'Get-ItemProperty returns properties for a file' {
        $props = Get-ItemProperty -LiteralPath $File
        $props | Should -Not -BeNullOrEmpty
    }
}

