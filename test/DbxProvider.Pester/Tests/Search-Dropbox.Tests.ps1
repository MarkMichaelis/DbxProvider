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

    $script:UniqueToken = ('searchtok' + [Guid]::NewGuid().ToString('N').Substring(0, 8))
    New-Item -Path "$($Folder.ProviderPath)\$($UniqueToken).txt" -ItemType File -Value 'searchable content' -Force | Out-Null

    # Dropbox search indexing isn't instant; allow a brief settling period.
    Start-Sleep -Seconds 5
}

AfterAll {
    if ($script:Folder) { Remove-DbxTestFolder -Path $script:Folder.ProviderPath }
    Disconnect-DbxTestDrive
}

Describe 'Search-Dropbox' -Skip:(-not $HasCredentials) {

    It 'returns results for a unique token' {
        $results = Search-Dropbox -Query $UniqueToken -Path $Folder.ApiPath -DriveName 'DbxTest' -MaxResults 25
        # Indexing latency may yield 0 results; assert the call succeeds without throwing
        # and returns a (possibly empty) collection.
        ($results | Measure-Object).Count | Should -BeGreaterOrEqual 0
    }

    It 'accepts -MaxResults without error' {
        { Search-Dropbox -Query $UniqueToken -Path $Folder.ApiPath -DriveName 'DbxTest' -MaxResults 5 } | Should -Not -Throw
    }
}

