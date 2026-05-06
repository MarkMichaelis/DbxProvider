BeforeDiscovery {
    Import-Module (Join-Path $PSScriptRoot '..\Helpers\TestEnvironment.psm1') -Force
    $script:HasCredentials = [bool]((Get-DbxTestSecrets).RefreshToken) -and [bool]((Get-DbxTestSecrets).AppKey)
    $script:RunLarge = [bool]((Get-DbxTestSecrets).RunLargeFileTests)
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

    It 'uploads a large (~160 MB) file via chunked session' {
        if (-not $RunLarge) {
            Set-ItResult -Skipped -Because 'Set DBX_RUN_LARGE_FILE_TESTS=1 (env var or user-secret) to enable the 160 MB chunked-upload test.'
            return
        }
        $local = Join-Path $TestDrive 'large.bin'
        $fs = [System.IO.File]::OpenWrite($local)
        try {
            $buf = New-Object byte[] (1MB)
            for ($i = 0; $i -lt 160; $i++) { $fs.Write($buf, 0, $buf.Length) }
        }
        finally { $fs.Dispose() }

        $remote = "$($Folder.ApiPath)/large.bin"
        $result = Invoke-DropboxUpload -Source $local -DropboxPath $remote -DriveName 'DbxTest' -WriteMode overwrite
        $result | Should -Not -BeNullOrEmpty
    }
}

