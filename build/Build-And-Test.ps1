#requires -Version 7.4
<#
.SYNOPSIS
    Build the DbxProvider solution and run the C# functional + Pester test suites.

.DESCRIPTION
    Single entry point used by both local developers and CI. Restores, builds,
    then optionally runs the xUnit functional tests and the Pester suite.

.PARAMETER Configuration
    dotnet build configuration. Defaults to Release.

.PARAMETER SkipFunctional
    Skip the xUnit functional test project.

.PARAMETER SkipPester
    Skip the Pester PowerShell test suite.

.PARAMETER IncludeLargeFileTests
    Set DBX_RUN_LARGE_FILE_TESTS=1 so large (>150 MB) upload tests are not skipped.
#>
[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [switch]$SkipFunctional,
    [switch]$SkipPester,
    [switch]$IncludeLargeFileTests,
    [switch]$AllowMissingSecrets
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
Write-Host "Repository root: $repoRoot" -ForegroundColor Cyan

$solution           = Join-Path $repoRoot 'DbxProvider.sln'
$functionalCsproj   = Join-Path $repoRoot 'test\DbxProvider.FunctionalTests\DbxProvider.FunctionalTests.csproj'
$pesterEntry        = Join-Path $repoRoot 'test\DbxProvider.Pester\Invoke-Tests.ps1'
$resultsDir         = Join-Path $repoRoot 'TestResults'
$moduleDll          = Join-Path $repoRoot 'src\DbxProvider\bin\Release\net8.0\DbxProvider.dll'

function Test-DropboxSecretsConfigured {
    foreach ($n in 'DBX_APP_KEY','DBX_APP_SECRET','DBX_REFRESH_TOKEN') {
        if ([string]::IsNullOrWhiteSpace([Environment]::GetEnvironmentVariable($n))) {
            $missing = $true; break
        } else { $missing = $false }
    }
    if (-not $missing) { return @{ Source = 'environment variables'; Ok = $true } }

    if (Test-Path $functionalCsproj) {
        try {
            $json = & dotnet user-secrets list --json --project $functionalCsproj 2>$null
            if ($LASTEXITCODE -eq 0 -and $json) {
                $obj = ($json -join "`n").Trim() | ConvertFrom-Json -ErrorAction Stop
                $names = $obj.PSObject.Properties.Name
                if (('DBX_APP_KEY' -in $names) -and ('DBX_APP_SECRET' -in $names) -and ('DBX_REFRESH_TOKEN' -in $names)) {
                    return @{ Source = "dotnet user-secrets ($functionalCsproj)"; Ok = $true }
                }
            }
        } catch { }
    }

    if (Test-Path $moduleDll) {
        # NOTE: do NOT Add-Type the module DLL here - that locks
        # bin\Release\net8.0\DbxProvider.dll for the lifetime of this
        # pwsh process and breaks every subsequent `dotnet build`.
        # Run the credential probe in a child pwsh so the DLL is
        # released as soon as the child exits.
        try {
            $probe = @'
param([string]$Dll)
try {
    Add-Type -Path $Dll -ErrorAction Stop
    $stored = [DbxProvider.Services.CredentialStore]::Load()
    if ($stored -and $stored.AppKey -and $stored.AppSecret -and $stored.RefreshToken) {
        [pscustomobject]@{ Ok = $true; Path = [DbxProvider.Services.CredentialStore]::CredentialFilePath } | ConvertTo-Json -Compress
    }
} catch { }
'@
            $probeScript = Join-Path ([System.IO.Path]::GetTempPath()) ("dbx-credprobe-" + [guid]::NewGuid().ToString('N') + ".ps1")
            Set-Content -LiteralPath $probeScript -Value $probe -Encoding UTF8
            try {
                $out = & pwsh -NoProfile -NonInteractive -ExecutionPolicy Bypass -File $probeScript -Dll $moduleDll 2>$null
            } finally {
                Remove-Item -LiteralPath $probeScript -Force -ErrorAction SilentlyContinue
            }
            if ($out) {
                $info = ($out -join "`n").Trim() | ConvertFrom-Json -ErrorAction Stop
                if ($info.Ok) {
                    return @{ Source = "CredentialStore ($($info.Path))"; Ok = $true }
                }
            }
        } catch { }
    }

    return @{ Source = $null; Ok = $false }
}

function Show-MissingSecretsHelp {
    Write-Host ""
    Write-Host '================================================================' -ForegroundColor Yellow
    Write-Host ' No Dropbox credentials configured for the test suites.' -ForegroundColor Yellow
    Write-Host '================================================================' -ForegroundColor Yellow
    Write-Host ''
    Write-Host 'The functional and Pester suites require AppKey, AppSecret, and a' -ForegroundColor Yellow
    Write-Host 'long-lived RefreshToken. Without them, every test that hits the' -ForegroundColor Yellow
    Write-Host 'Dropbox API will be SKIPPED (the only test that runs is the' -ForegroundColor Yellow
    Write-Host 'secret-leak guard).' -ForegroundColor Yellow
    Write-Host ''
    Write-Host 'Pick ONE of the following to provide credentials:' -ForegroundColor Cyan
    Write-Host ''
    Write-Host '  [1] Easiest -- authenticate once with Connect-Dropbox:' -ForegroundColor Cyan
    Write-Host '        dotnet build src\DbxProvider\DbxProvider.csproj -c Release'
    Write-Host '        Import-Module .\src\DbxProvider\bin\Release\net8.0\DbxProvider.dll'
    Write-Host '        Connect-Dropbox -AppKey <key> -AppSecret <secret>'
    Write-Host '      (Saves to %LOCALAPPDATA%\DbxProvider\credentials.json, DPAPI-encrypted.)'
    Write-Host ''
    Write-Host '  [2] dotnet user-secrets (mirrors how CI sees its secrets):' -ForegroundColor Cyan
    Write-Host '        pwsh ./build/Set-LocalSecrets.ps1'
    Write-Host ''
    Write-Host '  [3] Environment variables (one-off shell):' -ForegroundColor Cyan
    Write-Host '        $env:DBX_APP_KEY="..."; $env:DBX_APP_SECRET="..."; $env:DBX_REFRESH_TOKEN="..."'
    Write-Host ''
    Write-Host 'Then re-run:  pwsh ./build/Build-And-Test.ps1' -ForegroundColor Cyan
    Write-Host ''
    Write-Host 'To proceed anyway (e.g. just to run the secret-leak guard test),' -ForegroundColor DarkGray
    Write-Host 'pass -AllowMissingSecrets.' -ForegroundColor DarkGray
    Write-Host ''
}

if ($IncludeLargeFileTests) {
    $env:DBX_RUN_LARGE_FILE_TESTS = '1'
    Write-Host 'DBX_RUN_LARGE_FILE_TESTS=1 (large file tests enabled)' -ForegroundColor Yellow
}

$exitCodes = [System.Collections.Generic.List[int]]::new()

function Invoke-Step {
    param(
        [Parameter(Mandatory)] [string]$Name,
        [Parameter(Mandatory)] [scriptblock]$Action
    )
    Write-Host ""
    Write-Host "==> $Name" -ForegroundColor Green
    & $Action
    $code = $LASTEXITCODE
    if ($null -eq $code) { $code = 0 }
    Write-Host "    exit code: $code"
    $exitCodes.Add([int]$code) | Out-Null
}

Push-Location $repoRoot
$script:CredBackupPath  = $null
$script:CredOriginalPath = $null
try {
    Invoke-Step 'dotnet restore' { dotnet restore $solution }
    if ($exitCodes[-1] -ne 0) {
        throw "dotnet restore failed with exit code $($exitCodes[-1])."
    }

    Invoke-Step "dotnet build ($Configuration)" {
        # The csproj has an AfterTargets="Build" target that compiles
        # docs/help/en-US/*.md into MAML next to the DLL via Build-Help.ps1.
        # Help.Tests.ps1 depends on that MAML, so we must NOT pass
        # DbxSkipHelpBuild=true here -- the help-completeness gate is part
        # of the test surface.
        dotnet build $solution -c $Configuration --nologo
    }
    if ($exitCodes[-1] -ne 0) {
        throw "dotnet build failed with exit code $($exitCodes[-1])."
    }

    $secretsCheck = Test-DropboxSecretsConfigured
    if ($secretsCheck.Ok) {
        Write-Host ""
        Write-Host "==> Dropbox credentials detected via $($secretsCheck.Source)." -ForegroundColor Green
    } else {
        Show-MissingSecretsHelp
        if (-not $AllowMissingSecrets) {
            Write-Error 'Aborting: no Dropbox credentials configured. Use -AllowMissingSecrets to proceed anyway.'
            exit 2
        }
        Write-Host '==> Proceeding without credentials (-AllowMissingSecrets).' -ForegroundColor DarkYellow
    }

    # ------------------------------------------------------------------
    # Credential preservation (defense-in-depth):
    #
    # 1. Hoist credentials into process env vars so the test runs see them
    #    even if any test deletes or rewrites the credentials file
    #    mid-run. Both the xUnit suite and TestEnvironment.psm1 prefer
    #    env vars over CredentialStore, so this also makes credentials
    #    survive any in-test $env:LOCALAPPDATA redirect.
    # 2. Back up the on-disk credentials file before tests run and
    #    restore it from the backup if it was deleted, modified, or
    #    moved by a misbehaving test. The restore runs in a finally
    #    block so it covers Ctrl-C and unhandled exceptions.
    # ------------------------------------------------------------------
    $script:CredBackupPath  = $null
    $script:CredOriginalPath = $null
    if ($secretsCheck.Ok -and (Test-Path $moduleDll)) {
        try {
            # IMPORTANT: do NOT Add-Type the module DLL in this parent pwsh
            # process - that locks bin\Release\net8.0\DbxProvider.dll for
            # the lifetime of the session and breaks every subsequent
            # `dotnet build` (MSB3027 "file is locked by PowerShell 7").
            # Instead, shell out to a child pwsh process for the one-shot
            # credential read; the child exits and releases the DLL.
            $loadScript = @'
param([string]$Dll)
Add-Type -Path $Dll -ErrorAction Stop
$stored = [DbxProvider.Services.CredentialStore]::Load()
$path   = [DbxProvider.Services.CredentialStore]::CredentialFilePath
[pscustomobject]@{
    AppKey       = if ($stored) { $stored.AppKey }       else { $null }
    AppSecret    = if ($stored) { $stored.AppSecret }    else { $null }
    RefreshToken = if ($stored) { $stored.RefreshToken } else { $null }
    Path         = $path
} | ConvertTo-Json -Compress
'@
            $tempScript = Join-Path ([System.IO.Path]::GetTempPath()) ("dbx-credload-" + [guid]::NewGuid().ToString('N') + ".ps1")
            Set-Content -LiteralPath $tempScript -Value $loadScript -Encoding UTF8
            try {
                $json = & pwsh -NoProfile -NonInteractive -ExecutionPolicy Bypass -File $tempScript -Dll $moduleDll 2>$null
            } finally {
                Remove-Item -LiteralPath $tempScript -Force -ErrorAction SilentlyContinue
            }
            if ($json) {
                $stored = $json | ConvertFrom-Json
                if (-not $env:DBX_APP_KEY       -and $stored.AppKey)       { $env:DBX_APP_KEY       = $stored.AppKey }
                if (-not $env:DBX_APP_SECRET    -and $stored.AppSecret)    { $env:DBX_APP_SECRET    = $stored.AppSecret }
                if (-not $env:DBX_REFRESH_TOKEN -and $stored.RefreshToken) { $env:DBX_REFRESH_TOKEN = $stored.RefreshToken }
                $script:CredOriginalPath = $stored.Path
                if ($script:CredOriginalPath -and (Test-Path -LiteralPath $script:CredOriginalPath)) {
                    $script:CredBackupPath = Join-Path ([System.IO.Path]::GetTempPath()) ("dbxprovider-credbak-" + [guid]::NewGuid().ToString('N') + ".bin")
                    Copy-Item -LiteralPath $script:CredOriginalPath -Destination $script:CredBackupPath -Force
                    Write-Host "==> Credentials file backed up to '$script:CredBackupPath' (will be restored after tests)." -ForegroundColor DarkGreen
                }
            }
        } catch {
            Write-Warning "Could not snapshot credentials for restore-after-tests: $_"
        }
    }

    if (-not $SkipFunctional) {
        if (-not (Test-Path $functionalCsproj)) {
            Write-Warning "Functional test project not found at $functionalCsproj; skipping."
        } else {
            New-Item -ItemType Directory -Force -Path $resultsDir | Out-Null

            # Seed persistent fixtures (e.g. /DbxProviderFixtures/Exportable.paper)
            # before running tests that read them. Idempotent + best-effort:
            # never fails the build if Dropbox is unreachable.
            if ($secretsCheck.Ok) {
                $seedScript = Join-Path $PSScriptRoot 'Seed-DbxTestFixtures.ps1'
                if (Test-Path -LiteralPath $seedScript) {
                    Invoke-Step 'Seed persistent test fixtures' {
                        try { & $seedScript } catch { Write-Warning "Seed step failed (non-fatal): $_" }
                    }
                }
            }

            Invoke-Step 'dotnet test (functional)' {
                dotnet test $functionalCsproj `
                    -c $Configuration `
                    --no-build `
                    --logger 'trx;LogFileName=Functional.trx' `
                    --results-directory $resultsDir
            }
        }
    } else {
        Write-Host '==> Skipping functional tests (-SkipFunctional)' -ForegroundColor DarkYellow
    }

    if (-not $SkipPester) {
        if (-not (Test-Path $pesterEntry)) {
            Write-Warning "Pester entry not found at $pesterEntry; skipping."
        } else {
            $hasPester = Get-Module -ListAvailable -Name Pester |
                Where-Object { $_.Version -ge [version]'5.5.0' }
            if (-not $hasPester) {
                Write-Host '==> Installing Pester 5.5+' -ForegroundColor Green
                Install-Module Pester -MinimumVersion 5.5.0 -Force -Scope CurrentUser -SkipPublisherCheck
            }
            Invoke-Step 'Pester suite' {
                pwsh -NoProfile -File $pesterEntry
            }
        }
    } else {
        Write-Host '==> Skipping Pester tests (-SkipPester)' -ForegroundColor DarkYellow
    }
}
finally {
    # Restore credentials file from backup if a test deleted/clobbered it.
    if ($script:CredOriginalPath -and $script:CredBackupPath -and (Test-Path -LiteralPath $script:CredBackupPath)) {
        try {
            $needRestore = $true
            if (Test-Path -LiteralPath $script:CredOriginalPath) {
                $origHash   = (Get-FileHash -LiteralPath $script:CredOriginalPath -Algorithm SHA256).Hash
                $backupHash = (Get-FileHash -LiteralPath $script:CredBackupPath  -Algorithm SHA256).Hash
                $needRestore = ($origHash -ne $backupHash)
            }
            if ($needRestore) {
                $parent = Split-Path -Parent $script:CredOriginalPath
                if (-not (Test-Path -LiteralPath $parent)) {
                    New-Item -ItemType Directory -Path $parent -Force | Out-Null
                }
                Copy-Item -LiteralPath $script:CredBackupPath -Destination $script:CredOriginalPath -Force
                Write-Host "==> Credentials file restored from backup ('$script:CredOriginalPath')." -ForegroundColor DarkGreen
            }
            Remove-Item -LiteralPath $script:CredBackupPath -Force -ErrorAction SilentlyContinue
        } catch {
            Write-Warning "Failed to restore credentials backup '$script:CredBackupPath': $_"
        }
    }
    Pop-Location
}

$failed = @($exitCodes | Where-Object { $_ -ne 0 })
if ($failed.Count -gt 0) {
    Write-Host ""
    Write-Error "One or more steps failed (exit codes: $($exitCodes -join ', '))."
    exit 1
}

Write-Host ""
Write-Host 'All steps succeeded.' -ForegroundColor Green
exit 0
