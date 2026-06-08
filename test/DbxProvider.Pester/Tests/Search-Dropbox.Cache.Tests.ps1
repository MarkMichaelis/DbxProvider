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
    $script:Folder = New-DbxTestFolder -TestName 'Search'

    $script:UniqueToken = ('findtok' + [Guid]::NewGuid().ToString('N').Substring(0, 8))
    New-Item -Path "$($Folder.ProviderPath)\$($UniqueToken).txt" -ItemType File -Value 'findable content' -Force | Out-Null

    # Search-Dropbox reads the local metadata cache by default (zero API
    # enumeration), so warm the cache entry for this folder before searching. A
    # Get-ChildItem write-through populates the entry's items_json deterministically
    # (unlike the -NoCache server index, there is no indexing latency to wait out).
    Get-ChildItem -LiteralPath $Folder.ProviderPath | Out-Null
}

AfterAll {
    if ($script:Folder) { Remove-DbxTestFolder -Path $script:Folder.ProviderPath }
    Disconnect-DbxTestDrive
}

Describe 'Search-Dropbox (cache default)' -Skip:(-not $HasCredentials) {

    It 'finds a uniquely-named cached file by wildcard query' {
        $results = @(Search-Dropbox "*$UniqueToken*" -DriveName 'DbxTest')
        ($results | Measure-Object).Count | Should -BeGreaterOrEqual 1
    }

    It 'finds a uniquely-named cached file by plain substring query' {
        $results = @(Search-Dropbox $UniqueToken -DriveName 'DbxTest')
        ($results | Measure-Object).Count | Should -BeGreaterOrEqual 1
    }

    It 'returns nothing for a token that matches no item' {
        $results = @(Search-Dropbox "*no-such-$UniqueToken-zzz*" -DriveName 'DbxTest')
        ($results | Measure-Object).Count | Should -Be 0
    }

    It 'accepts a -Path subtree without error' {
        { Search-Dropbox '*' -Path $Folder.ApiPath -DriveName 'DbxTest' } | Should -Not -Throw
    }

    It 'accepts -ZeroByteOnly without error' {
        { Search-Dropbox '*' -ZeroByteOnly -DriveName 'DbxTest' } | Should -Not -Throw
    }
}
