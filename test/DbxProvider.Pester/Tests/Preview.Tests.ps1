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
    $script:Folder = New-DbxTestFolder -TestName 'Preview'

    # Preview/Thumbnail require a previewable file. Plain text isn't supported by
    # the preview API, so most assertions tolerate the cmdlet writing an error.
    New-Item -Path "$($Folder.ProviderPath)\preview.txt" -ItemType File -Value 'preview content' -Force | Out-Null
    $script:ApiPath = "$($Folder.ApiPath)/preview.txt"
}

AfterAll {
    if ($script:Folder) { Remove-DbxTestFolder -Path $script:Folder.ProviderPath }
    Disconnect-DbxTestDrive
}

Describe 'Dropbox Preview/Thumbnail cmdlets' -Skip:(-not $HasCredentials) {

    It 'Get-DropboxPreview executes (may fail for unsupported types)' {
        # The cmdlet writes a non-terminating error rather than throwing for
        # unsupported file types, so we just assert it does not throw.
        { Get-DropboxPreview -Path $ApiPath -DriveName 'DbxTest' -ErrorAction SilentlyContinue | Out-Null } | Should -Not -Throw
    }

    It 'Get-DropboxThumbnail executes (may fail for unsupported types)' {
        { Get-DropboxThumbnail -Path $ApiPath -Size 'w64h64' -Format jpeg -DriveName 'DbxTest' -ErrorAction SilentlyContinue | Out-Null } | Should -Not -Throw
    }
}

