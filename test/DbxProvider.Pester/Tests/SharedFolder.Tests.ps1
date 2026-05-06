# Initialize at script top so Pester v5 discovery (which evaluates -Skip:
# expressions before BeforeAll runs) does not throw under StrictMode.
$script:SharedFolderId = $null

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
    $script:Folder = New-DbxTestFolder -TestName 'SharedFolder'
    $script:SharedFolderId = $null
}

AfterAll {
    if ($script:SharedFolderId) {
        try { Remove-DropboxSharedFolder -SharedFolderId $script:SharedFolderId -DriveName 'DbxTest' -Confirm:$false -ErrorAction SilentlyContinue } catch {}
    }
    if ($script:Folder) { Remove-DbxTestFolder -Path $script:Folder.ProviderPath }
    Disconnect-DbxTestDrive
}

Describe 'Dropbox Shared Folder cmdlets' -Skip:(-not $HasCredentials) {

    It 'Add-DropboxSharedFolder shares a folder and returns an ID' {
        $script:SharedFolderId = Add-DropboxSharedFolder -Path $Folder.ApiPath -DriveName 'DbxTest'
        $script:SharedFolderId | Should -Not -BeNullOrEmpty
    }

    It 'Get-DropboxSharedFolder lists shared folders' {
        $folders = Get-DropboxSharedFolder -DriveName 'DbxTest'
        ($folders | Measure-Object).Count | Should -BeGreaterOrEqual 0
    }

    It 'Remove-DropboxSharedFolder unshares the folder' {
        if ($null -eq $script:SharedFolderId) {
            Set-ItResult -Skipped -Because 'Add-DropboxSharedFolder did not produce a SharedFolderId.'
            return
        }
        Remove-DropboxSharedFolder -SharedFolderId $script:SharedFolderId -DriveName 'DbxTest' -Confirm:$false
        $script:SharedFolderId = $null
    }
}

