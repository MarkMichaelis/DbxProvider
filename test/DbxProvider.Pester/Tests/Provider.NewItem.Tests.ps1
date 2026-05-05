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
    $script:Folder = New-DbxTestFolder -TestName 'NewItem'
}

AfterAll {
    if ($script:Folder) { Remove-DbxTestFolder -Path $script:Folder.ProviderPath }
    Disconnect-DbxTestDrive
}

Describe 'Provider New-Item' -Skip:(-not $HasCredentials) {

    It 'creates a new folder' {
        $path = "$($Folder.ProviderPath)\sub-folder"
        New-Item -Path $path -ItemType Directory | Out-Null
        Test-Path -LiteralPath $path | Should -BeTrue
    }

    It 'creates a new file with -Value' {
        $path = "$($Folder.ProviderPath)\hello.txt"
        New-Item -Path $path -ItemType File -Value 'hello world' | Out-Null
        Test-Path -LiteralPath $path | Should -BeTrue
        ((Get-Content -LiteralPath $path) -join "`n").TrimEnd() | Should -Be 'hello world'
    }

    It 'creates an empty file' {
        $path = "$($Folder.ProviderPath)\empty.txt"
        New-Item -Path $path -ItemType File | Out-Null
        Test-Path -LiteralPath $path | Should -BeTrue
    }
}

