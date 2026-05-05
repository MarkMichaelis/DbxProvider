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
    $script:Folder = New-DbxTestFolder -TestName 'Link'
    New-Item -Path "$($Folder.ProviderPath)\link.txt" -ItemType File -Value 'link content' -Force | Out-Null
    $script:ApiPath = "$($Folder.ApiPath)/link.txt"
}

AfterAll {
    if ($script:Folder) { Remove-DbxTestFolder -Path $script:Folder.ProviderPath }
    Disconnect-DbxTestDrive
}

Describe 'Dropbox Link cmdlets' -Skip:(-not $HasCredentials) {

    It 'Get-DropboxTemporaryLink returns a URL' {
        $url = Get-DropboxTemporaryLink -Path $ApiPath -DriveName 'DbxTest'
        $url | Should -Not -BeNullOrEmpty
        $url | Should -Match '^https?://'
    }

    It 'Save-DropboxUrl starts an async save job' {
        $remote = "$($Folder.ApiPath)/saved-url.txt"
        try {
            $job = Save-DropboxUrl -DropboxPath $remote -Url 'https://httpbin.org/bytes/64' -DriveName 'DbxTest' -ErrorAction Stop
            $job | Should -Not -BeNullOrEmpty
        }
        catch {
            $msg = $_.Exception.Message
            if ($msg -match 'RetryException|unexpected error|download_failed|too_many_requests|other/\.\.|temporary failure') {
                Set-ItResult -Skipped -Because "Dropbox /save_url returned a transient/server-side error: $msg"
                return
            }
            throw
        }
    }
}

