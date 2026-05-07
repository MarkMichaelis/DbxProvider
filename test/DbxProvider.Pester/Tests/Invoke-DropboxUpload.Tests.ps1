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
    $script:Folder = New-DbxTestFolder -TestName 'Upload'
}

AfterAll {
    if ($script:Folder) { Remove-DbxTestFolder -Path $script:Folder.ProviderPath }
    Disconnect-DbxTestDrive
}

Describe 'Invoke-DropboxUpload' -Skip:(-not $HasCredentials) {

    It 'uploads a small local file' {
        $local = Join-Path $TestDrive 'small.txt'
        Set-Content -LiteralPath $local -Value 'small upload'

        $remote = "$($Folder.ApiPath)/small.txt"
        $result = Invoke-DropboxUpload -Source $local -DropboxPath $remote -DriveName 'DbxTest' -WriteMode overwrite
        $result | Should -Not -BeNullOrEmpty
        Test-Path -LiteralPath "$($Folder.ProviderPath)\small.txt" | Should -BeTrue
    }

    # Note: chunked upload-session (>150 MB) coverage lives in the xUnit
    # functional suite (UploadDownloadTests.ChunkedUpload_RoundTrip), which
    # uses internal test hooks to dial the 150 MB threshold down to 1 MB.
    # That keeps CI fast (~5 s instead of ~3 min) while still exercising
    # UploadSessionStartAsync / AppendV2Async / FinishAsync end-to-end.
}


