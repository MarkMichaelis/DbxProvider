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
    $script:Folder = New-DbxTestFolder -TestName 'RestoreRevision'

    $script:File = "$($Folder.ProviderPath)\rev.txt"
    New-Item -Path $script:File -ItemType File -Value 'v1' -Force | Out-Null
    Start-Sleep -Seconds 1
    # File exists now, so Set-Content correctly dispatches to the Dropbox provider.
    Set-Content -LiteralPath $script:File -Value 'v2'
    $script:ApiPath = "$($Folder.ApiPath)/rev.txt"
}

AfterAll {
    if ($script:Folder) { Remove-DbxTestFolder -Path $script:Folder.ProviderPath }
    Disconnect-DbxTestDrive
}

Describe 'Restore-DropboxRevision' -Skip:(-not $HasCredentials) {

    It 'restores a file to a previous revision' {
        $revs = Get-DropboxRevision -Path $ApiPath -DriveName 'DbxTest' -Limit 10
        ($revs | Measure-Object).Count | Should -BeGreaterOrEqual 1

        $oldest = ($revs | Select-Object -Last 1)
        $oldRev = $oldest.Rev
        $oldRev | Should -Not -BeNullOrEmpty

        $restored = Restore-DropboxRevision -Path $ApiPath -Rev $oldRev -DriveName 'DbxTest' -Confirm:$false
        $restored | Should -Not -BeNullOrEmpty
    }
}

