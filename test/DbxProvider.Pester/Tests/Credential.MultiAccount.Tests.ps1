BeforeAll {
    Import-Module Pester
    Import-Module (Join-Path $PSScriptRoot '..\Helpers\TestEnvironment.psm1') -Force

    # Redirect LOCALAPPDATA to a test-isolated directory so we never touch the
    # developer's real credentials file.
    $script:OrigLocalAppData = $env:LOCALAPPDATA
    $script:TempLocalAppData = Join-Path ([System.IO.Path]::GetTempPath()) ("DbxProviderMultiAcctTest-" + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $script:TempLocalAppData -Force | Out-Null
    $env:LOCALAPPDATA = $script:TempLocalAppData

    Import-Module (Get-DbxProviderModulePath) -Force -DisableNameChecking -Global

    $expectedRoot = Join-Path $script:TempLocalAppData 'DbxProvider'
    $actualPath   = [DbxProvider.Services.CredentialStore]::CredentialFilePath
    if (-not $actualPath.StartsWith($expectedRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
        $env:LOCALAPPDATA = $script:OrigLocalAppData
        throw "Multi-account credential test sandbox redirect failed (path was '$actualPath')."
    }
}

AfterAll {
    try { Remove-DropboxCredential -All -Confirm:$false -ErrorAction SilentlyContinue } catch {}
    if ($null -ne $script:OrigLocalAppData) { $env:LOCALAPPDATA = $script:OrigLocalAppData }
    if ($script:TempLocalAppData -and (Test-Path -LiteralPath $script:TempLocalAppData)) {
        Remove-Item -LiteralPath $script:TempLocalAppData -Recurse -Force -ErrorAction SilentlyContinue
    }
}

Describe 'Multi-account credential cmdlets' {

    BeforeEach {
        # Wipe between tests so each starts with a clean store.
        Remove-DropboxCredential -All -Confirm:$false -ErrorAction SilentlyContinue
    }

    It 'Set-DropboxCredential -Account stores entries keyed by accountId' {
        Set-DropboxCredential -Account 'dbid:aaa' -AppKey 'k1' -AppSecret 's1' -RefreshToken 'rt1'
        Set-DropboxCredential -Account 'dbid:bbb' -AppKey 'k2' -AppSecret 's2' -RefreshToken 'rt2'

        $all = Get-DropboxCredential -All -AsPlainText
        $all.Count | Should -Be 2
        ($all | Where-Object AccountId -EQ 'dbid:aaa').RefreshToken | Should -Be 'rt1'
        ($all | Where-Object AccountId -EQ 'dbid:bbb').RefreshToken | Should -Be 'rt2'
    }

    It 'Get-DropboxCredential without -Account returns the default account' {
        Set-DropboxCredential -Account 'dbid:aaa' -AppKey 'k1' -RefreshToken 'rt1'
        Set-DropboxCredential -Account 'dbid:bbb' -AppKey 'k2' -RefreshToken 'rt2'

        $default = Get-DropboxCredential -AsPlainText
        $default.AccountId | Should -Be 'dbid:aaa'   # first saved is default
        $default.IsDefault | Should -BeTrue
    }

    It 'Set-DropboxCredential -SetDefault changes the default account' {
        Set-DropboxCredential -Account 'dbid:aaa' -AppKey 'k1' -RefreshToken 'rt1'
        Set-DropboxCredential -Account 'dbid:bbb' -AppKey 'k2' -RefreshToken 'rt2' -SetDefault

        (Get-DropboxCredential).AccountId | Should -Be 'dbid:bbb'
    }

    It 'Get-DropboxCredential -Account looks up by accountId or email' {
        # Pre-create stubs by accountId, then "enrich" the second with an email
        # by saving it under a Set-DropboxCredential call that includes the email
        # via accountId selector (the cmdlet preserves AccountId/Email when
        # merging — we set them here through the underlying API for test clarity).
        Set-DropboxCredential -Account 'dbid:aaa' -AppKey 'k1' -RefreshToken 'rt1'
        [DbxProvider.Services.CredentialStore]::SaveAccount(
            (New-Object DbxProvider.Services.StoredAccount -Property @{
                AccountId = 'dbid:bbb'; Email = 'bob@example.com'; AppKey = 'k2'; RefreshToken = 'rt2'
            }), $false)

        (Get-DropboxCredential -Account 'dbid:bbb' -AsPlainText).RefreshToken | Should -Be 'rt2'
        (Get-DropboxCredential -Account 'bob@example.com' -AsPlainText).RefreshToken | Should -Be 'rt2'
        (Get-DropboxCredential -Account 'bob' -AsPlainText).RefreshToken | Should -Be 'rt2'
    }

    It 'Remove-DropboxCredential -Account removes only that entry' {
        Set-DropboxCredential -Account 'dbid:aaa' -AppKey 'k1' -RefreshToken 'rt1'
        Set-DropboxCredential -Account 'dbid:bbb' -AppKey 'k2' -RefreshToken 'rt2'

        Remove-DropboxCredential -Account 'dbid:aaa' -Confirm:$false

        $remaining = Get-DropboxCredential -All
        $remaining.Count | Should -Be 1
        $remaining[0].AccountId | Should -Be 'dbid:bbb'
        $remaining[0].IsDefault | Should -BeTrue
    }

    It 'Remove-DropboxCredential -All clears every saved account' {
        Set-DropboxCredential -Account 'dbid:aaa' -AppKey 'k1'
        Set-DropboxCredential -Account 'dbid:bbb' -AppKey 'k2'

        Remove-DropboxCredential -All -Confirm:$false

        Get-DropboxCredential -All | Should -BeNullOrEmpty
    }

    It 'Selector ambiguity (same email local-part) is reported as an error' {
        [DbxProvider.Services.CredentialStore]::SaveAccount(
            (New-Object DbxProvider.Services.StoredAccount -Property @{
                AccountId = 'dbid:aaa'; Email = 'mark@one.com'; AppKey = 'k1'
            }), $false)
        [DbxProvider.Services.CredentialStore]::SaveAccount(
            (New-Object DbxProvider.Services.StoredAccount -Property @{
                AccountId = 'dbid:bbb'; Email = 'mark@two.com'; AppKey = 'k2'
            }), $false)

        { Get-DropboxCredential -Account 'mark' -ErrorAction Stop } |
            Should -Throw -ExpectedMessage '*ambiguous*' -Because 'two emails share local-part "mark"'
    }
}
