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
    $script:Folder = New-DbxTestFolder -TestName 'SharedLink'
    New-Item -Path "$($Folder.ProviderPath)\sl.txt" -ItemType File -Value 'shared link' -Force | Out-Null
    $script:ApiPath = "$($Folder.ApiPath)/sl.txt"
    $script:CreatedUrls = @()
}

AfterAll {
    foreach ($u in $script:CreatedUrls) {
        try { Remove-DropboxSharedLink -Url $u -DriveName 'DbxTest' -Confirm:$false -ErrorAction SilentlyContinue } catch {}
    }
    if ($script:Folder) { Remove-DbxTestFolder -Path $script:Folder.ProviderPath }
    Disconnect-DbxTestDrive
}

Describe 'Dropbox Shared Link cmdlets' -Skip:(-not $HasCredentials) {

    It 'New-DropboxSharedLink creates a link' {
        $link = New-DropboxSharedLink -Path $ApiPath -DriveName 'DbxTest'
        $link | Should -Not -BeNullOrEmpty
        $link.Url | Should -Not -BeNullOrEmpty
        $script:CreatedUrls += $link.Url
    }

    It 'Get-DropboxSharedLink lists links for the path' {
        $links = Get-DropboxSharedLink -Path $ApiPath -DriveName 'DbxTest'
        ($links | Measure-Object).Count | Should -BeGreaterOrEqual 1
    }

    It 'Remove-DropboxSharedLink revokes a link' {
        # Dropbox returns shared_link_already_exists when re-creating a link
        # for a path that already has one; reuse the existing link in that case.
        try {
            $link = New-DropboxSharedLink -Path $ApiPath -DriveName 'DbxTest' -ErrorAction Stop
        }
        catch {
            if ($_.Exception.Message -match 'shared_link_already_exists') {
                $link = Get-DropboxSharedLink -Path $ApiPath -DriveName 'DbxTest' | Select-Object -First 1
            }
            else { throw }
        }
        $link.Url | Should -Not -BeNullOrEmpty
        Remove-DropboxSharedLink -Url $link.Url -DriveName 'DbxTest' -Confirm:$false
        # Subsequent revoke of same URL should not throw uncaught (cmdlet writes non-terminating error).
    }
}

