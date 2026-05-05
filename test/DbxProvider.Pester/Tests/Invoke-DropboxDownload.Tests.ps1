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
    $script:Folder = New-DbxTestFolder -TestName 'Download'
    New-Item -Path "$($Folder.ProviderPath)\download.txt" -ItemType File -Value 'roundtrip' -Force | Out-Null
}

AfterAll {
    if ($script:Folder) { Remove-DbxTestFolder -Path $script:Folder.ProviderPath }
    Disconnect-DbxTestDrive
}

Describe 'Invoke-DropboxDownload' -Skip:(-not $HasCredentials) {

    It 'downloads a file to local disk' {
        $dest = Join-Path $TestDrive 'download.txt'
        Invoke-DropboxDownload -Path "$($Folder.ApiPath)/download.txt" -Destination $dest -DriveName 'DbxTest' -Force | Out-Null
        Test-Path -LiteralPath $dest | Should -BeTrue
        (Get-Content -LiteralPath $dest -Raw).TrimEnd() | Should -Be 'roundtrip'
    }

    It 'fails to overwrite without -Force' {
        $dest = Join-Path $TestDrive 'download2.txt'
        Set-Content -LiteralPath $dest -Value 'pre-existing'
        { Invoke-DropboxDownload -Path "$($Folder.ApiPath)/download.txt" -Destination $dest -DriveName 'DbxTest' -ErrorAction Stop } | Should -Throw
    }
}

