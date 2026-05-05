BeforeDiscovery {
    Import-Module (Join-Path $PSScriptRoot '..\Helpers\TestEnvironment.psm1') -Force
    $secrets = Get-DbxTestSecrets
    $script:HasCredentials = [bool]($secrets.RefreshToken) -and [bool]($secrets.AppKey)
    $script:HasMember = [bool]($secrets.TestMemberEmail)
}

BeforeAll {
    Import-Module Pester
    Import-Module (Join-Path $PSScriptRoot '..\Helpers\TestEnvironment.psm1') -Force
    Import-Module (Join-Path $PSScriptRoot '..\Helpers\TestRoot.psm1') -Force
    $script:Secrets = Get-DbxTestSecrets
    if (-not $script:Secrets.RefreshToken -or -not $script:Secrets.TestMemberEmail) { return }
    Import-Module (Get-DbxProviderModulePath) -Force -DisableNameChecking -Global
    Connect-DbxTestDrive
    Initialize-DbxTestRoot
    $script:Folder = New-DbxTestFolder -TestName 'Member'
    $script:SharedFolderId = Add-DropboxSharedFolder -Path $Folder.ApiPath -DriveName 'DbxTest'
}

AfterAll {
    if ($script:SharedFolderId) {
        try { Remove-DropboxSharedFolder -SharedFolderId $script:SharedFolderId -DriveName 'DbxTest' -Confirm:$false -ErrorAction SilentlyContinue } catch {}
    }
    if ($script:Folder) { Remove-DbxTestFolder -Path $script:Folder.ProviderPath }
    if ($script:Secrets -and $script:Secrets.RefreshToken) { Disconnect-DbxTestDrive }
}

Describe 'Dropbox Member cmdlets' -Skip:(-not $HasCredentials -or -not $HasMember) {

    It 'Add-DropboxMember adds a member to a shared folder' {
        { Add-DropboxMember -SharedFolderId $SharedFolderId -Email $Secrets.TestMemberEmail -AccessLevel viewer -DriveName 'DbxTest' } | Should -Not -Throw
    }

    It 'Get-DropboxMember lists members of the shared folder' {
        $members = Get-DropboxMember -SharedFolderId $SharedFolderId -DriveName 'DbxTest'
        ($members | Measure-Object).Count | Should -BeGreaterOrEqual 0
    }

    It 'Remove-DropboxMember removes the member' {
        { Remove-DropboxMember -SharedFolderId $SharedFolderId -Email $Secrets.TestMemberEmail -DriveName 'DbxTest' -Confirm:$false } | Should -Not -Throw
    }
}

