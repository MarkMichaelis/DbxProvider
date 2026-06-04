Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$script:RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..')).Path
$script:ModuleDllPath = Join-Path $script:RepoRoot 'src\DbxProvider\bin\Release\net8.0\DbxProvider.dll'
$script:FunctionalTestsCsproj = Join-Path $script:RepoRoot 'test\DbxProvider.FunctionalTests\DbxProvider.FunctionalTests.csproj'
$script:CachedSecrets = $null

function Get-DbxTestSecrets {
    [CmdletBinding()]
    param(
        [switch]$Refresh
    )

    if ($script:CachedSecrets -and -not $Refresh) {
        return $script:CachedSecrets
    }

    $secrets = [ordered]@{
        AppKey             = $env:DBX_APP_KEY
        AppSecret          = $env:DBX_APP_SECRET
        RefreshToken       = $env:DBX_REFRESH_TOKEN
        TestMemberEmail    = $env:DBX_TEST_MEMBER_EMAIL
    }

    $needMore = -not $secrets.AppKey -or -not $secrets.AppSecret -or `
        -not $secrets.RefreshToken -or -not $secrets.TestMemberEmail

    if ($needMore -and (Test-Path $script:FunctionalTestsCsproj)) {
        try {
            $dotnet = Get-Command dotnet -ErrorAction Stop
            $json = & $dotnet.Path user-secrets list --json --project $script:FunctionalTestsCsproj 2>$null
            if ($LASTEXITCODE -eq 0 -and $json) {
                $joined = ($json -join "`n").Trim()
                if ($joined) {
                    $obj = $joined | ConvertFrom-Json -ErrorAction Stop
                    foreach ($prop in $obj.PSObject.Properties) {
                        switch ($prop.Name) {
                            'DBX_APP_KEY'             { if (-not $secrets.AppKey)          { $secrets.AppKey          = $prop.Value } }
                            'DBX_APP_SECRET'          { if (-not $secrets.AppSecret)       { $secrets.AppSecret       = $prop.Value } }
                            'DBX_REFRESH_TOKEN'       { if (-not $secrets.RefreshToken)    { $secrets.RefreshToken    = $prop.Value } }
                            'DBX_TEST_MEMBER_EMAIL'   { if (-not $secrets.TestMemberEmail) { $secrets.TestMemberEmail = $prop.Value } }
                        }
                    }
                }
            }
        }
        catch {
            Write-Verbose "Could not read user-secrets: $_"
        }
    }

    $needMore = -not $secrets.AppKey -or -not $secrets.AppSecret -or -not $secrets.RefreshToken
    if ($needMore) {
        try {
            if (-not ('IntelliTect.Dropbox.CredentialStore' -as [type])) {
                if (Test-Path $script:ModuleDllPath) {
                    Import-DbxProviderModule
                }
            }
            $storeType = 'IntelliTect.Dropbox.CredentialStore' -as [type]
            if ($storeType) {
                $stored = $storeType::Load()
                if ($stored) {
                    if (-not $secrets.AppKey)       { $secrets.AppKey       = $stored.AppKey }
                    if (-not $secrets.AppSecret)    { $secrets.AppSecret    = $stored.AppSecret }
                    if (-not $secrets.RefreshToken) { $secrets.RefreshToken = $stored.RefreshToken }
                }
            }
        }
        catch {
            Write-Verbose "Could not read CredentialStore: $_"
        }
    }

    $script:CachedSecrets = [pscustomobject]$secrets
    return $script:CachedSecrets
}

function Import-DbxProviderModule {
    [CmdletBinding()]
    param()

    if (-not (Test-Path $script:ModuleDllPath)) {
        throw "DbxProvider.dll not found at '$script:ModuleDllPath'. Build the module first with 'dotnet build -c Release src\DbxProvider\DbxProvider.csproj'."
    }

    if (Get-Module -Name DbxProvider) {
        Remove-Module DbxProvider -Force -ErrorAction SilentlyContinue
    }

    # NOTE: Importing a binary module from inside another module's function
    # binds the cmdlets to the *caller* module's session state, even with
    # -Global. Tests should call Import-Module on the path returned by
    # Get-DbxProviderModulePath directly from their BeforeAll. This function
    # is kept for backward compatibility / non-test callers.
    Import-Module $script:ModuleDllPath -Force -DisableNameChecking -Global
}

function Get-DbxProviderModulePath {
    [CmdletBinding()]
    param()
    if (-not (Test-Path $script:ModuleDllPath)) {
        throw "DbxProvider.dll not found at '$script:ModuleDllPath'. Build the module first with 'dotnet build -c Release src\DbxProvider\DbxProvider.csproj'."
    }
    return $script:ModuleDllPath
}

function Connect-DbxTestDrive {
    [CmdletBinding()]
    param(
        [string]$DriveName = 'DbxTest'
    )

    $secrets = Get-DbxTestSecrets
    if (-not $secrets.RefreshToken -or -not $secrets.AppKey) {
        throw "Cannot connect: missing DBX_APP_KEY or DBX_REFRESH_TOKEN."
    }

    if (Get-PSDrive -Name $DriveName -ErrorAction SilentlyContinue) {
        Disconnect-DbxTestDrive -DriveName $DriveName
    }

    Connect-Dropbox `
        -AppKey       $secrets.AppKey `
        -AppSecret    $secrets.AppSecret `
        -RefreshToken $secrets.RefreshToken `
        -DriveName    $DriveName `
        -NoSave | Out-Null
}

function Disconnect-DbxTestDrive {
    [CmdletBinding()]
    param(
        [string]$DriveName = 'DbxTest'
    )

    if (Get-PSDrive -Name $DriveName -ErrorAction SilentlyContinue) {
        try {
            Disconnect-Dropbox -DriveName $DriveName -ErrorAction SilentlyContinue | Out-Null
        }
        catch {
            Write-Verbose "Disconnect-Dropbox failed: $_"
        }
    }
}

Export-ModuleMember -Function Get-DbxTestSecrets, Import-DbxProviderModule, Get-DbxProviderModulePath, Connect-DbxTestDrive, Disconnect-DbxTestDrive
