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
    $script:Folder = New-DbxTestFolder -TestName 'Tag'
    New-Item -Path "$($Folder.ProviderPath)\tagged.txt" -ItemType File -Value 'tag me' -Force | Out-Null
    $script:ApiPath = "$($Folder.ApiPath)/tagged.txt"
    $script:TagName = ('t' + [Guid]::NewGuid().ToString('N').Substring(0, 8))
}

AfterAll {
    if ($script:Folder) { Remove-DbxTestFolder -Path $script:Folder.ProviderPath }
    Disconnect-DbxTestDrive
}

Describe 'Dropbox Tag cmdlets' -Skip:(-not $HasCredentials) {

    It 'Add-DropboxTag adds a tag to a file' {
        { Add-DropboxTag -Path $ApiPath -Tag $TagName -DriveName 'DbxTest' } | Should -Not -Throw
    }

    It 'Get-DropboxTag lists tags for the file' {
        $tags = Get-DropboxTag -Path $ApiPath -DriveName 'DbxTest'
        ($tags | Measure-Object).Count | Should -BeGreaterOrEqual 0
    }

    It 'Remove-DropboxTag removes the tag' {
        { Remove-DropboxTag -Path $ApiPath -Tag $TagName -DriveName 'DbxTest' -Confirm:$false } | Should -Not -Throw
    }
}

