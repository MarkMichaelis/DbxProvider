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
    $script:Folder = New-DbxTestFolder -TestName 'Batch'

    1..3 | ForEach-Object {
        New-Item -Path "$($script:Folder.ProviderPath)\batch-$_.txt" -ItemType File -Value "batch $_" -Force | Out-Null
    }
}

AfterAll {
    if ($script:Folder) { Remove-DbxTestFolder -Path $script:Folder.ProviderPath }
    Disconnect-DbxTestDrive
}

Describe 'Dropbox Batch cmdlets' -Skip:(-not $HasCredentials) {

    It 'Copy-DropboxItemBatch copies multiple files' {
        $from = 1..3 | ForEach-Object { "$($Folder.ApiPath)/batch-$_.txt" }
        $to   = 1..3 | ForEach-Object { "$($Folder.ApiPath)/copy-$_.txt"  }
        { Copy-DropboxItemBatch -FromPath $from -ToPath $to -DriveName 'DbxTest' | Out-Null } | Should -Not -Throw
    }

    It 'Move-DropboxItemBatch moves multiple files' {
        $from = 1..3 | ForEach-Object { "$($Folder.ApiPath)/copy-$_.txt" }
        $to   = 1..3 | ForEach-Object { "$($Folder.ApiPath)/moved-$_.txt" }
        { Move-DropboxItemBatch -FromPath $from -ToPath $to -DriveName 'DbxTest' | Out-Null } | Should -Not -Throw
    }

    It 'Remove-DropboxItemBatch deletes multiple files' {
        $paths = 1..3 | ForEach-Object { "$($Folder.ApiPath)/moved-$_.txt" }
        { Remove-DropboxItemBatch -Path $paths -DriveName 'DbxTest' -Confirm:$false | Out-Null } | Should -Not -Throw
    }
}

