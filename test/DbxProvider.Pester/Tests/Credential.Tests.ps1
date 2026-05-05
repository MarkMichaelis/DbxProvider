BeforeAll {
    Import-Module Pester
    Import-Module (Join-Path $PSScriptRoot '..\Helpers\TestEnvironment.psm1') -Force

    # Redirect LOCALAPPDATA to a test-isolated directory so the credential file
    # cannot clobber a developer's real saved credentials. CredentialStore now
    # honors $env:LOCALAPPDATA when set, so this redirect is effective.
    $script:OrigLocalAppData = $env:LOCALAPPDATA
    $script:TempLocalAppData = Join-Path ([System.IO.Path]::GetTempPath()) ("DbxProviderCredTest-" + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $script:TempLocalAppData -Force | Out-Null
    $env:LOCALAPPDATA = $script:TempLocalAppData

    Import-Module (Get-DbxProviderModulePath) -Force -DisableNameChecking -Global

    # Verify the redirect actually took effect before letting any test run.
    $expectedRoot = Join-Path $script:TempLocalAppData 'DbxProvider'
    $actualPath   = [DbxProvider.Services.CredentialStore]::CredentialFilePath
    if (-not $actualPath.StartsWith($expectedRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
        $env:LOCALAPPDATA = $script:OrigLocalAppData
        throw "Credential test sandbox redirect failed (path was '$actualPath'). Aborting to protect real credentials."
    }
}

AfterAll {
    try {
        Remove-DropboxCredential -Confirm:$false -ErrorAction SilentlyContinue
    } catch {}
    if ($null -ne $script:OrigLocalAppData) {
        $env:LOCALAPPDATA = $script:OrigLocalAppData
    }
    if ($script:TempLocalAppData -and (Test-Path -LiteralPath $script:TempLocalAppData)) {
        Remove-Item -LiteralPath $script:TempLocalAppData -Recurse -Force -ErrorAction SilentlyContinue
    }
}

Describe 'Dropbox Credential cmdlets' {

    It 'Set-DropboxCredential persists values that Get-DropboxCredential returns' {
        Set-DropboxCredential -AppKey 'test-key' -AppSecret 'test-secret' -RefreshToken 'test-refresh'
        $cred = Get-DropboxCredential -AsPlainText
        $cred | Should -Not -BeNullOrEmpty
        $cred.AppKey       | Should -Be 'test-key'
        $cred.AppSecret    | Should -Be 'test-secret'
        $cred.RefreshToken | Should -Be 'test-refresh'
    }

    It 'Get-DropboxCredential masks AppSecret/RefreshToken without -AsPlainText' {
        $cred = Get-DropboxCredential
        $cred.AppSecret    | Should -Not -Be 'test-secret'
        $cred.RefreshToken | Should -Not -Be 'test-refresh'
    }

    It 'Remove-DropboxCredential clears the saved credentials' {
        Remove-DropboxCredential -Confirm:$false
        $cred = Get-DropboxCredential
        $cred | Should -BeNullOrEmpty
    }
}

