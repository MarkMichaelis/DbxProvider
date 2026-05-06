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
    $script:Folder = New-DbxTestFolder -TestName 'RemoveItem'
}

AfterAll {
    if ($script:Folder) { Remove-DbxTestFolder -Path $script:Folder.ProviderPath }
    Disconnect-DbxTestDrive
}

Describe 'Provider Remove-Item' -Skip:(-not $HasCredentials) {

    It 'removes a file (soft delete)' {
        $file = "$($Folder.ProviderPath)\to-remove.txt"
        New-Item -Path $file -ItemType File -Value 'bye' -Force | Out-Null
        Remove-Item -LiteralPath $file
        Test-Path -LiteralPath $file | Should -BeFalse
    }

    It 'removes an empty folder' {
        $sub = "$($Folder.ProviderPath)\empty-folder"
        New-Item -Path $sub -ItemType Directory | Out-Null
        Remove-Item -LiteralPath $sub
        Test-Path -LiteralPath $sub | Should -BeFalse
    }

    It 'removes a folder recursively' {
        $sub = "$($Folder.ProviderPath)\with-content"
        New-Item -Path $sub -ItemType Directory | Out-Null
        New-Item -Path "$sub\inner.txt" -ItemType File -Value 'x' -Force | Out-Null
        Remove-Item -LiteralPath $sub -Recurse
        Test-Path -LiteralPath $sub | Should -BeFalse
    }

    It 'removes a file with -Force (skips confirmation)' {
        $file = "$($Folder.ProviderPath)\force-remove.txt"
        New-Item -Path $file -ItemType File -Value 'bye' -Force | Out-Null
        Remove-Item -LiteralPath $file -Force -ErrorAction Stop
        Test-Path -LiteralPath $file | Should -BeFalse
    }
}

