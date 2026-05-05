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
    $script:Folder = New-DbxTestFolder -TestName 'Cache'

    # Seed: a couple of files plus a sub-folder so listings are non-trivial.
    New-Item -Path "$($Folder.ProviderPath)\one.txt" -ItemType File -Value 'one' | Out-Null
    New-Item -Path "$($Folder.ProviderPath)\two.txt" -ItemType File -Value 'two' | Out-Null
    New-Item -Path "$($Folder.ProviderPath)\sub"     -ItemType Directory      | Out-Null

    # Cache entries are keyed off the provider path (backslash-separated).
    $script:CacheKey = ($Folder.ProviderPath -replace '^DbxTest:', '')

    # Pester runs It blocks in their own scope, so define the helper as a
    # script-scoped scriptblock that we invoke with `& $GetCacheRow ...`.
    $script:GetCacheRow = {
        param([string]$KeySuffix, [string]$DriveName = 'DbxTest')
        $needle = $KeySuffix.Replace('/', '\').TrimEnd('\').ToLowerInvariant()
        Get-DropboxCacheInfo -DriveName $DriveName |
            Where-Object {
                $_.PSObject.Properties['Path'] -and
                $_.Path -and
                $_.Path.Replace('/', '\').TrimEnd('\').ToLowerInvariant().EndsWith($needle)
            }
    }

    # Allow Dropbox a moment to propagate the new folder before the first list.
    Start-Sleep -Milliseconds 500

    # Pester v5 only resolves functions that are defined inside BeforeAll
    # (top-level `function` declarations are not visible to It blocks).
    function Find-CacheRow {
        param([Parameter(Mandatory)][string]$KeySuffix, [string]$DriveName = 'DbxTest')
        $needle = $KeySuffix.Replace('/', '\').TrimEnd('\').ToLowerInvariant()
        Get-DropboxCacheInfo -DriveName $DriveName |
            Where-Object {
                $_.PSObject.Properties['Path'] -and
                $_.Path -and
                $_.Path.Replace('/', '\').TrimEnd('\').ToLowerInvariant().EndsWith($needle)
            }
    }
}

AfterAll {
    if ($script:Folder) { Remove-DbxTestFolder -Path $script:Folder.ProviderPath }
    Disconnect-DbxTestDrive
}

Describe 'Provider Metadata Cache' -Skip:(-not $HasCredentials) {

    It 'populates a cache entry on first Get-ChildItem' {
        Clear-DropboxCache -DriveName DbxTest
        Get-ChildItem -LiteralPath $Folder.ProviderPath | Out-Null

        $row = Find-CacheRow -KeySuffix $Folder.Name
        $row | Should -Not -BeNullOrEmpty
        $row.ItemCount | Should -BeGreaterOrEqual 3
    }

    It 'returns identical results on warm reads' {
        Clear-DropboxCache -DriveName DbxTest
        $cold = @(Get-ChildItem -LiteralPath $Folder.ProviderPath | ForEach-Object Name | Sort-Object)
        $warm = @(Get-ChildItem -LiteralPath $Folder.ProviderPath | ForEach-Object Name | Sort-Object)
        ($warm -join '|') | Should -Be ($cold -join '|')
    }

    It 'reflects an external add via Invoke-DropboxUpload on the next read' {
        Get-ChildItem -LiteralPath $Folder.ProviderPath | Out-Null  # warm

        $tmp = New-TemporaryFile
        try {
            Set-Content -LiteralPath $tmp.FullName -Value 'externally-added'
            # Invoke-DropboxUpload bypasses the provider's write-through hooks,
            # so the cache must validate-on-read to surface the new file.
            Invoke-DropboxUpload -Source $tmp.FullName `
                -DropboxPath "$($Folder.ApiPath)/external-add.txt" `
                -DriveName DbxTest | Out-Null
        }
        finally {
            Remove-Item -LiteralPath $tmp.FullName -Force -ErrorAction SilentlyContinue
        }

        $names = (Get-ChildItem -LiteralPath $Folder.ProviderPath).Name
        $names | Should -Contain 'external-add.txt'
    }

    It 'reflects a Remove-Item delete on the next read' {
        $name = "to-delete-$([guid]::NewGuid().ToString('N').Substring(0,8)).txt"
        New-Item -Path "$($Folder.ProviderPath)\$name" -ItemType File -Value 'doomed' | Out-Null
        Get-ChildItem -LiteralPath $Folder.ProviderPath | Out-Null  # warm

        # Remove-Item without -Force routes through soft-delete (the test app
        # lacks the files.permanent_delete scope).
        Remove-Item -LiteralPath "$($Folder.ProviderPath)\$name"

        $names = (Get-ChildItem -LiteralPath $Folder.ProviderPath).Name
        $names | Should -Not -Contain $name
    }

    It 'Clear-DropboxCache drops the entry' {
        Get-ChildItem -LiteralPath $Folder.ProviderPath | Out-Null
        (Find-CacheRow -KeySuffix $Folder.Name) | Should -Not -BeNullOrEmpty

        Clear-DropboxCache -Path $CacheKey -DriveName DbxTest

        (Find-CacheRow -KeySuffix $Folder.Name) | Should -BeNullOrEmpty
    }

    It 'Update-DropboxCache runs without throwing' {
        Get-ChildItem -LiteralPath $Folder.ProviderPath | Out-Null
        { Update-DropboxCache -Path $CacheKey -DriveName DbxTest } | Should -Not -Throw
    }

    It 'Set-DropboxCacheOption -Disable bypasses the cache' {
        Clear-DropboxCache -DriveName DbxTest
        Set-DropboxCacheOption -Disable -DriveName DbxTest | Out-Null
        try {
            Get-ChildItem -LiteralPath $Folder.ProviderPath | Out-Null
            Get-ChildItem -LiteralPath $Folder.ProviderPath | Out-Null

            (Find-CacheRow -KeySuffix $Folder.Name) | Should -BeNullOrEmpty
        }
        finally {
            Set-DropboxCacheOption -Enable -DriveName DbxTest | Out-Null
        }
    }

    It 'write-through after Remove-Item updates the cached snapshot' {
        $name = "write-thru-$([guid]::NewGuid().ToString('N').Substring(0,8)).txt"
        New-Item -Path "$($Folder.ProviderPath)\$name" -ItemType File -Value 'x' | Out-Null
        Get-ChildItem -LiteralPath $Folder.ProviderPath | Out-Null  # warm

        Remove-Item -LiteralPath "$($Folder.ProviderPath)\$name"

        $row = Find-CacheRow -KeySuffix $Folder.Name
        $row | Should -Not -BeNullOrEmpty

        $names = (Get-ChildItem -LiteralPath $Folder.ProviderPath).Name
        $names | Should -Not -Contain $name
    }

    It 'survives Disconnect/Connect via on-disk hydration' {
        Get-ChildItem -LiteralPath $Folder.ProviderPath | Out-Null  # warm + dirty
        # The cache flushes dirty entries on Dispose (i.e. Disconnect-Dropbox).
        Disconnect-DbxTestDrive
        Connect-DbxTestDrive

        (Find-CacheRow -KeySuffix $Folder.Name) | Should -Not -BeNullOrEmpty
    }
}
