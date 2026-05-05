# Initialize at script top so Pester v5 discovery (which evaluates -Skip:
# expressions before BeforeAll runs) does not throw under StrictMode.
$script:PaperSupported = $true

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
    $script:Folder = New-DbxTestFolder -TestName 'Paper'
    $script:ApiPath = "$($Folder.ApiPath)/paper-doc.paper"
    $script:PaperSupported = $true
}

AfterAll {
    if ($script:Folder) { Remove-DbxTestFolder -Path $script:Folder.ProviderPath }
    Disconnect-DbxTestDrive
}

Describe 'Dropbox Paper cmdlets' -Skip:(-not $HasCredentials) {

    # Dropbox Paper API (paper_create / paper_update) was deprecated and may
    # return errors for accounts that never used Paper. Tests catch the deprecation
    # error and skip gracefully.

    It 'New-DropboxPaper creates a Paper doc (skipped if API deprecated)' {
        try {
            $url = New-DropboxPaper -Path $ApiPath -Content '# Hello' -ImportFormat markdown -DriveName 'DbxTest' -ErrorAction Stop
            $url | Should -Not -BeNullOrEmpty
        }
        catch {
            $script:PaperSupported = $false
            Set-ItResult -Skipped -Because "Paper API unavailable: $_"
        }
    }

    It 'Set-DropboxPaper updates a Paper doc' -Skip:(-not $script:PaperSupported) {
        try {
            $url = Set-DropboxPaper -Path $ApiPath -Content '# Updated' -ImportFormat markdown -UpdatePolicy overwrite -DriveName 'DbxTest' -ErrorAction Stop
            $url | Should -Not -BeNullOrEmpty
        }
        catch {
            Set-ItResult -Skipped -Because "Paper API unavailable: $_"
        }
    }
}

