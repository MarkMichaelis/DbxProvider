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
    $script:Folder = New-DbxTestFolder -TestName 'Lock'
    New-Item -Path "$($Folder.ProviderPath)\lockme.txt" -ItemType File -Value 'lock target' -Force | Out-Null
    $script:ApiPath = "$($Folder.ApiPath)/lockme.txt"
}

AfterAll {
    try { Unlock-DropboxFile -Path $script:ApiPath -DriveName 'DbxTest' -ErrorAction SilentlyContinue | Out-Null } catch {}
    if ($script:Folder) { Remove-DbxTestFolder -Path $script:Folder.ProviderPath }
    Disconnect-DbxTestDrive
}

Describe 'Dropbox File Lock cmdlets' -Skip:(-not $HasCredentials) {

    It 'Lock-DropboxFile locks a file' {
        try {
            Lock-DropboxFile -Path $ApiPath -DriveName 'DbxTest' -ErrorAction Stop | Out-Null
        }
        catch {
            $msg = $_.Exception.Message
            if ($msg -match 'other/\.|missing_scope|insufficient_plan|access_denied|business') {
                Set-ItResult -Skipped -Because "File locking requires Dropbox Business / appropriate scope: $msg"
                return
            }
            throw
        }
    }

    It 'Get-DropboxFileLock returns lock status' {
        try {
            Get-DropboxFileLock -Path $ApiPath -DriveName 'DbxTest' -ErrorAction Stop | Out-Null
        }
        catch {
            $msg = $_.Exception.Message
            if ($msg -match 'other/\.|missing_scope|insufficient_plan|access_denied|business') {
                Set-ItResult -Skipped -Because "File locking requires Dropbox Business / appropriate scope: $msg"
                return
            }
            throw
        }
    }

    It 'Unlock-DropboxFile unlocks the file' {
        try {
            Unlock-DropboxFile -Path $ApiPath -DriveName 'DbxTest' -ErrorAction Stop | Out-Null
        }
        catch {
            $msg = $_.Exception.Message
            if ($msg -match 'other/\.|missing_scope|insufficient_plan|access_denied|business|not_locked') {
                Set-ItResult -Skipped -Because "File locking requires Dropbox Business / file not locked: $msg"
                return
            }
            throw
        }
    }
}

