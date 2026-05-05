[CmdletBinding()]
<#
.SYNOPSIS
    Demonstrates DbxProvider's rate-limit retry + cancellation behavior live.

.DESCRIPTION
    Builds the module (Release), imports it, connects to a real Dropbox
    drive using stored credentials (env vars or the FunctionalTests
    user-secrets), and runs Get-ChildItem against the drive. The
    DBX_SIMULATE_RATELIMIT environment variable injects synthetic 429s
    so you can watch the warning, the wait, and the eventual success
    without actually triggering Dropbox throttling.

    Three scenarios:

      -Mode Quick   3 fake rate-limit hits, 5 seconds each (default).
      -Mode Long    1 fake rate-limit hit, 60 seconds — long enough to
                    test Ctrl+C cancellation during the wait.
      -Mode Real    No simulation. Hits real Dropbox; rate limiting
                    only fires if the API decides to throttle you.

.EXAMPLE
    pwsh -File build\Demo-RateLimitRetry.ps1
    Quick demo: see 3 warnings, 5s each, then a directory listing.

.EXAMPLE
    pwsh -File build\Demo-RateLimitRetry.ps1 -Mode Long
    Wait starts; press Ctrl+C while the warning is showing — the
    pipeline should stop cleanly with a "pipeline stopped" error.

.EXAMPLE
    pwsh -File build\Demo-RateLimitRetry.ps1 -Mode Real -RemotePath '/Apps/MyApp'
    Hit real Dropbox at the given path (no fake 429s).
#>
param(
    [ValidateSet('Quick','Long','Real','Hammer')]
    [string]$Mode = 'Quick',

    [string]$RemotePath = '/',

    [string]$DriveName = 'DbxDemo',

    [int]$HammerCount = 500,

    [switch]$SkipBuild
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$csproj   = Join-Path $repoRoot 'src\DbxProvider\DbxProvider.csproj'
$moduleDll = Join-Path $repoRoot 'src\DbxProvider\bin\Release\net8.0\DbxProvider.dll'
$helpers  = Join-Path $repoRoot 'test\DbxProvider.Pester\Helpers\TestEnvironment.psm1'

function Write-Section([string]$text) {
    Write-Host ''
    Write-Host ('=' * 72) -ForegroundColor Cyan
    Write-Host "[$([DateTime]::Now.ToString('HH:mm:ss'))] $text" -ForegroundColor Cyan
    Write-Host ('=' * 72) -ForegroundColor Cyan
}

# 1. Build (Release, matches the path Pester helpers expect).
if (-not $SkipBuild) {
    Write-Section "Building DbxProvider (Release)"
    $env:DbxSkipHelpBuild = 'true'
    & dotnet build $csproj -c Release --nologo -v minimal
    if ($LASTEXITCODE -ne 0) { throw "Build failed." }
}
if (-not (Test-Path $moduleDll)) {
    throw "Module DLL not found at '$moduleDll'. Re-run without -SkipBuild."
}

# 2. Resolve credentials via the existing test helper (env vars or user-secrets).
Write-Section "Loading Dropbox credentials"
Import-Module $helpers -Force
$secrets = Get-DbxTestSecrets
if (-not $secrets.RefreshToken -or -not $secrets.AppKey -or -not $secrets.AppSecret) {
    throw @"
Missing Dropbox credentials. Set the following env vars (or store them in
the FunctionalTests user-secrets):
  DBX_APP_KEY, DBX_APP_SECRET, DBX_REFRESH_TOKEN
"@
}
Write-Host "Found AppKey: $($secrets.AppKey.Substring(0,4))*** (refresh token present)"

# 3. Make sure simulator is OFF for the connect step.
Write-Section "Configuring simulator (off until after Connect)"
Remove-Item Env:\DBX_SIMULATE_RATELIMIT -ErrorAction SilentlyContinue

# 4. Import the module fresh.
Write-Section "Importing module"
if (Get-Module -Name DbxProvider) { Remove-Module DbxProvider -Force }
Import-Module $moduleDll -Force -DisableNameChecking

# 5. Connect WITHOUT the simulator armed (Connect-Dropbox calls
#    GetCurrentAccountAsync which would otherwise consume our fake 429s).
Write-Section "Connecting to Dropbox (drive $DriveName)"
if (Get-PSDrive -Name $DriveName -ErrorAction SilentlyContinue) {
    Disconnect-Dropbox -DriveName $DriveName -ErrorAction SilentlyContinue | Out-Null
}
Connect-Dropbox `
    -AppKey       $secrets.AppKey `
    -AppSecret    $secrets.AppSecret `
    -RefreshToken $secrets.RefreshToken `
    -DriveName    $DriveName `
    -NoSave | Out-Null

# 6. NOW arm the simulator so the next provider/cmdlet call sees fake 429s.
Write-Section "Arming simulator for mode '$Mode'"
switch ($Mode) {
    'Quick' {
        $env:DBX_SIMULATE_RATELIMIT = '3:5'
        Write-Host "Will inject 3 fake 429s, 5s each (~15s total)." -ForegroundColor Yellow
        break
    }
    'Long' {
        $env:DBX_SIMULATE_RATELIMIT = '1:60'
        Write-Host "Will inject 1 fake 429, 60s wait." -ForegroundColor Yellow
        Write-Host "While the WARNING is showing, press Ctrl+C to test cancellation." -ForegroundColor Yellow
        break
    }
    'Real' {
        Write-Host "Simulator disabled — hitting real Dropbox only." -ForegroundColor Yellow
        break
    }
    'Hammer' {
        Write-Host "Simulator disabled — will fire $HammerCount real Dropbox calls in a tight loop to attempt to trigger a real 429." -ForegroundColor Yellow
        Write-Host "Press Ctrl+C any time to stop. WARNING: aggressive use can get your app temporarily throttled." -ForegroundColor Yellow
        break
    }
}

# 7. The actual demo call. -Verbose surfaces "attempt #N / cumulative wait" lines.
if ($Mode -eq 'Hammer') {
    Write-Section "Hammering $($DriveName):$RemotePath  ($HammerCount calls; Ctrl+C to stop)"
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $hits = 0
    try {
        for ($i = 1; $i -le $HammerCount; $i++) {
            try {
                # Use Get-DropboxAccount as a cheap, predictable endpoint.
                Get-DropboxAccount -DriveName $DriveName -ErrorAction Stop | Out-Null
            } catch {
                Write-Host "[$i] Caught: $($_.Exception.GetType().FullName): $($_.Exception.Message)" -ForegroundColor Red
                throw
            }
            if ($i % 25 -eq 0) {
                Write-Host ("[{0}] {1}/{2} calls done ({3:N1}s elapsed)" -f `
                    [DateTime]::Now.ToString('HH:mm:ss'), $i, $HammerCount, $sw.Elapsed.TotalSeconds)
            }
        }
    }
    catch {
        Write-Host ''
        Write-Host "Stopped: $($_.Exception.GetType().FullName): $($_.Exception.Message)" -ForegroundColor Yellow
    }
    finally {
        $sw.Stop()
        Write-Host ("Hammer finished. Elapsed: {0:N1}s. If you saw any 'Dropbox returned 429' WARNING above, the retry path fired against a real rate limit." -f $sw.Elapsed.TotalSeconds) -ForegroundColor Green
        Write-Section "Cleanup"
        Disconnect-Dropbox -DriveName $DriveName -ErrorAction SilentlyContinue | Out-Null
        Remove-Item Env:\DBX_SIMULATE_RATELIMIT -ErrorAction SilentlyContinue
        Write-Host "Done."
    }
    return
}

Write-Section "Listing $($DriveName):$RemotePath  (watch for WARNING + Ctrl+C)"
$sw = [System.Diagnostics.Stopwatch]::StartNew()
try {
    Get-ChildItem -Path "$($DriveName):$RemotePath" -Verbose |
        Select-Object -First 20 |
        Format-Table Name, ItemType, Size -AutoSize
}
catch {
    Write-Host ''
    Write-Host "Caught: $($_.Exception.GetType().FullName): $($_.Exception.Message)" -ForegroundColor Yellow
}
finally {
    $sw.Stop()
    Write-Host ("Elapsed: {0:N1}s" -f $sw.Elapsed.TotalSeconds) -ForegroundColor Green
    Write-Section "Cleanup"
    Disconnect-Dropbox -DriveName $DriveName -ErrorAction SilentlyContinue | Out-Null
    Remove-Item Env:\DBX_SIMULATE_RATELIMIT -ErrorAction SilentlyContinue
    Write-Host "Done."
}
