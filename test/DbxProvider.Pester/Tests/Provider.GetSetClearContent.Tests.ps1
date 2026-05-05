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
    $script:Folder = New-DbxTestFolder -TestName 'Content'
}

AfterAll {
    if ($script:Folder) { Remove-DbxTestFolder -Path $script:Folder.ProviderPath }
    Disconnect-DbxTestDrive
}

Describe 'Provider Get/Set/Clear-Content' -Skip:(-not $HasCredentials) {

    It 'Set-Content writes content and Get-Content reads it back' {
        $file = "$($Folder.ProviderPath)\set.txt"
        # PowerShell's Set-Content cannot dispatch to a custom provider for a
        # non-existent path (Resolve-Path returns nothing pre-creation), so we
        # seed the file via New-Item and then exercise Set-Content's overwrite.
        New-Item -Path $file -ItemType File -Value 'placeholder' -Force | Out-Null
        Set-Content -LiteralPath $file -Value 'first version'
        ((Get-Content -LiteralPath $file) -join "`n").TrimEnd() | Should -Be 'first version'
    }

    It 'Set-Content overwrites existing content' {
        $file = "$($Folder.ProviderPath)\overwrite.txt"
        New-Item -Path $file -ItemType File -Value 'placeholder' -Force | Out-Null
        Set-Content -LiteralPath $file -Value 'first'
        Set-Content -LiteralPath $file -Value 'second'
        ((Get-Content -LiteralPath $file) -join "`n").TrimEnd() | Should -Be 'second'
    }

    It 'Clear-Content empties the file' {
        $file = "$($Folder.ProviderPath)\clear.txt"
        New-Item -Path $file -ItemType File -Value 'to be cleared' -Force | Out-Null
        Clear-Content -LiteralPath $file
        $content = ((Get-Content -LiteralPath $file) -join "`n")
        if ($null -ne $content) { $content.Trim() | Should -BeNullOrEmpty }
    }
}

