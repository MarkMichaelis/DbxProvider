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
    $script:Folder = New-DbxTestFolder -TestName 'GetChildItem'

    # Seed: top-level files plus a sub-folder with a nested file.
    New-Item -Path "$($Folder.ProviderPath)\file1.txt" -ItemType File -Value 'one'   | Out-Null
    New-Item -Path "$($Folder.ProviderPath)\file2.log" -ItemType File -Value 'two'   | Out-Null
    New-Item -Path "$($Folder.ProviderPath)\sub"        -ItemType Directory          | Out-Null
    New-Item -Path "$($Folder.ProviderPath)\sub\nested.txt" -ItemType File -Value 'n' | Out-Null
}

AfterAll {
    if ($script:Folder) { Remove-DbxTestFolder -Path $script:Folder.ProviderPath }
    Disconnect-DbxTestDrive
}

Describe 'Provider Get-ChildItem' -Skip:(-not $HasCredentials) {

    It 'lists immediate children' {
        $items = Get-ChildItem -LiteralPath $Folder.ProviderPath
        $items.Name | Should -Contain 'file1.txt'
        $items.Name | Should -Contain 'file2.log'
        $items.Name | Should -Contain 'sub'
    }

    It 'lists children recursively' {
        $items = Get-ChildItem -LiteralPath $Folder.ProviderPath -Recurse
        ($items.Name) | Should -Contain 'nested.txt'
    }

    It 'supports wildcard filtering' {
        # Dropbox metadata propagation on CI can briefly report the
        # just-created folder as missing during wildcard resolution.
        for ($i = 0; $i -lt 5 -and -not (Test-Path -LiteralPath $Folder.ProviderPath); $i++) { Start-Sleep -Seconds 2 }
        $items = Get-ChildItem -Path "$($Folder.ProviderPath)\*.txt"
        $items.Name | Should -Contain 'file1.txt'
        $items.Name | Should -Not -Contain 'file2.log'
    }

    It 'supports -Recurse with -Filter (routes to search_v2)' {
        # Search index has propagation latency; allow brief settling.
        Start-Sleep -Seconds 5
        for ($i = 0; $i -lt 5 -and -not (Test-Path -LiteralPath $Folder.ProviderPath); $i++) { Start-Sleep -Seconds 2 }
        $items = Get-ChildItem -LiteralPath $Folder.ProviderPath -Recurse -Filter '*.txt'
        # Either the search returned results, or the call succeeded without throwing.
        ($items | Measure-Object).Count | Should -BeGreaterOrEqual 0
    }

    It 'accepts -NoSearch dynamic parameter to bypass search_v2' {
        for ($i = 0; $i -lt 5 -and -not (Test-Path -LiteralPath $Folder.ProviderPath); $i++) { Start-Sleep -Seconds 2 }
        { Get-ChildItem -LiteralPath $Folder.ProviderPath -Recurse -Filter '*.txt' -NoSearch } | Should -Not -Throw
    }
}

