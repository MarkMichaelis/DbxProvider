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
    $script:Folder = New-DbxTestFolder -TestName 'CopyMoveRename'
}

AfterAll {
    if ($script:Folder) { Remove-DbxTestFolder -Path $script:Folder.ProviderPath }
    Disconnect-DbxTestDrive
}

Describe 'Provider Copy/Move/Rename-Item' -Skip:(-not $HasCredentials) {

    It 'Copy-Item duplicates a file' {
        $src = "$($Folder.ProviderPath)\copy-src.txt"
        $dst = "$($Folder.ProviderPath)\copy-dst.txt"
        New-Item -Path $src -ItemType File -Value 'copy me' -Force | Out-Null
        Copy-Item -LiteralPath $src -Destination $dst
        Test-Path -LiteralPath $src | Should -BeTrue
        Test-Path -LiteralPath $dst | Should -BeTrue
    }

    It 'Move-Item relocates a file' {
        $src = "$($Folder.ProviderPath)\move-src.txt"
        $dst = "$($Folder.ProviderPath)\move-dst.txt"
        New-Item -Path $src -ItemType File -Value 'move me' -Force | Out-Null
        Move-Item -LiteralPath $src -Destination $dst
        Test-Path -LiteralPath $src | Should -BeFalse
        Test-Path -LiteralPath $dst | Should -BeTrue
    }

    It 'Rename-Item renames a file' {
        $src = "$($Folder.ProviderPath)\rename-src.txt"
        New-Item -Path $src -ItemType File -Value 'rename me' -Force | Out-Null
        Rename-Item -LiteralPath $src -NewName 'rename-dst.txt'
        Test-Path -LiteralPath $src | Should -BeFalse
        Test-Path -LiteralPath "$($Folder.ProviderPath)\rename-dst.txt" | Should -BeTrue
    }
}

