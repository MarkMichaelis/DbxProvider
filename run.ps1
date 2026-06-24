#Requires -Version 7.0
<#
.SYNOPSIS
    Build DbxProvider fresh and launch Find-DropboxConflicts.ps1 in a child pwsh.

.DESCRIPTION
    Your interactive pwsh session loads (and locks) DbxProvider.dll, so it keeps running
    the OLD build even after a rebuild -- and a fixed temp deploy copy can ALSO be locked
    by a previous child run, silently leaving you on stale bits.

    To make stale-core impossible, this script builds the module into a UNIQUE, timestamped
    output directory (which can never be locked by a prior run) and then starts a SEPARATE
    pwsh process that imports that freshly-built module. Console-output / cmdlet / core
    changes therefore always take effect without restarting your main shell.

    By default it runs the resume-delete smoke test:
        Find-DropboxConflicts.ps1 -Delete -SkipScan -Limit <Limit>

.PARAMETER Limit
    Value passed to -Limit. Default 100000.

.PARAMETER Configuration
    Build configuration. Default 'Debug'.

.PARAMETER NoBuild
    Skip the build and reuse the newest prior run-build (or -ModulePath if supplied). Use
    this only when you deliberately want to reuse an existing build.

.PARAMETER ModulePath
    Explicit path to a DbxProvider.psd1 to load. When supplied, the build is skipped and the
    given module is used verbatim. When omitted (default), a fresh build is produced.

.PARAMETER ScriptArgs
    Extra arguments appended verbatim to Find-DropboxConflicts.ps1, overriding/extending the
    defaults (e.g. -ScriptArgs '-WhatIf' or -ScriptArgs '-Limit',1000).

.PARAMETER NewWindow
    Launch in a separate visible window that stays open (-NoExit). Without this, the command
    runs in a child process attached to the current console and the window closes when done.

.PARAMETER NewTab
    Launch in a new Windows Terminal tab (via wt.exe) instead of a separate window, so the run
    sits alongside the current session. The tab stays open (-NoExit) so the live status line is
    watchable. Falls back to -NewWindow (with a warning) when wt.exe is unavailable.

.EXAMPLE
    .\run.ps1 -NewWindow
    Builds fresh and opens a separate window so you can watch the live status line.

.EXAMPLE
    .\run.ps1 -NewTab
    Builds fresh and opens a new Windows Terminal tab running the live status line.

.EXAMPLE
    .\run.ps1 -Limit 1000 -ScriptArgs '-WhatIf'
    Builds fresh and dry-runs 1000 items.
#>
[CmdletBinding()]
param(
    [int]      $Limit = 100000,
    [string]   $Configuration = 'Debug',
    [switch]   $NoBuild,
    [string]   $ModulePath,
    [string[]] $ScriptArgs,
    [switch]   $NewWindow,
    [switch]   $NewTab
)

$ErrorActionPreference = 'Stop'

function New-WtTabArgumentList {
    # Builds the wt.exe argument list that opens a child pwsh in a new Windows Terminal
    # tab. '-w 0' targets the current Windows Terminal window (creating one if none is
    # open); 'new-tab' adds a fresh tab there. Paths are quoted so spaces survive wt's
    # own command-line parsing. The child is launched via -File (not -Command) so the
    # status-line script's embedded ';' is never reinterpreted as a wt action delimiter.
    [CmdletBinding()]
    [OutputType([string[]])]
    param(
        [Parameter(Mandatory)][string]$PwshPath,
        [Parameter(Mandatory)][string]$ScriptPath,
        [Parameter(Mandatory)][string]$Title
    )

    return @(
        '-w', '0',
        'new-tab', '--title', $Title,
        ('"{0}"' -f $PwshPath),
        '-NoExit', '-NoProfile',
        '-File', ('"{0}"' -f $ScriptPath)
    )
}

# When dot-sourced (e.g. by Pester) load the helpers above but skip execution.
if ($MyInvocation.InvocationName -eq '.') { return }

$repoRoot = $PSScriptRoot
$script = Join-Path $repoRoot 'Find-DropboxConflicts.ps1'
if (-not (Test-Path -LiteralPath $script)) {
    throw "Find-DropboxConflicts.ps1 not found at '$script'."
}

$runBuildRoot = Join-Path $env:TEMP 'DbxProvider-run'

if ($ModulePath) {
    # Explicit module wins; never build over the top of it.
    if (-not (Test-Path -LiteralPath $ModulePath)) {
        throw "Module not found at '$ModulePath'."
    }
}
elseif ($NoBuild) {
    # Reuse the most recent fresh build, if any.
    $latest = Get-ChildItem -LiteralPath $runBuildRoot -Directory -ErrorAction SilentlyContinue |
        Sort-Object Name -Descending |
        Where-Object { Test-Path -LiteralPath (Join-Path $_.FullName 'DbxProvider.psd1') } |
        Select-Object -First 1
    if (-not $latest) {
        throw "No prior build found under '$runBuildRoot'. Run without -NoBuild to build one first."
    }
    $ModulePath = Join-Path $latest.FullName 'DbxProvider.psd1'
    Write-Host "Reusing prior build: $ModulePath" -ForegroundColor DarkGray
}
else {
    # Fresh build into a UNIQUE dir so it can never be locked by a prior child run.
    $stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
    $outDir = Join-Path $runBuildRoot $stamp
    $csproj = Join-Path $repoRoot 'src\DbxProvider\DbxProvider.csproj'

    Write-Host "Building $Configuration -> $outDir ..." -ForegroundColor Cyan
    $env:DOTNET_ROLL_FORWARD = 'LatestMajor'
    $env:DbxSkipHelpBuild = 'true'
    dotnet build $csproj -c $Configuration --output $outDir --nologo -v q
    if ($LASTEXITCODE -ne 0) {
        throw "Build failed (exit $LASTEXITCODE); not launching a stale module."
    }

    $ModulePath = Join-Path $outDir 'DbxProvider.psd1'
    if (-not (Test-Path -LiteralPath $ModulePath)) {
        throw "Build reported success but '$ModulePath' is missing."
    }
    Write-Host "Built: $ModulePath" -ForegroundColor Green

    # Best-effort tidy: keep the 5 most recent run-builds, drop older unlocked ones.
    Get-ChildItem -LiteralPath $runBuildRoot -Directory -ErrorAction SilentlyContinue |
        Sort-Object Name -Descending |
        Select-Object -Skip 5 |
        ForEach-Object { Remove-Item -LiteralPath $_.FullName -Recurse -Force -ErrorAction SilentlyContinue }
}

# Default invocation; -ScriptArgs (if any) are appended so the caller can override.
$invokeArgs = @('-Delete', '-SkipScan', '-Limit', $Limit)
if ($ScriptArgs) { $invokeArgs += $ScriptArgs }

# Build a single command string for the child pwsh. Quote paths to survive spaces.
$argLine = ($invokeArgs | ForEach-Object {
        if ($_ -is [string] -and $_ -match '\s') { '"{0}"' -f $_ } else { "$_" }
    }) -join ' '
$command = "Set-Location -LiteralPath `"$repoRoot`"; & `"$script`" -ModulePath `"$ModulePath`" $argLine"

$pwsh = (Get-Process -Id $PID).Path   # use the same pwsh executable that's running this script
$baseArgs = @('-NoProfile', '-Command', $command)

if ($NewTab) {
    $wt = Get-Command wt.exe -ErrorAction SilentlyContinue
    if ($wt) {
        # wt parses ';' as an action delimiter, so hand the child a temp -File script
        # (no embedded ';' on the wt command line) rather than an inline -Command.
        if (-not (Test-Path -LiteralPath $runBuildRoot)) {
            New-Item -ItemType Directory -Path $runBuildRoot -Force | Out-Null
        }
        $tabScript = Join-Path $runBuildRoot ("tab-{0}.ps1" -f (Get-Date -Format 'yyyyMMdd-HHmmss-fff'))
        Set-Content -LiteralPath $tabScript -Value $command -Encoding utf8
        # Keep only the few most recent tab scripts so they do not accumulate forever.
        Get-ChildItem -LiteralPath $runBuildRoot -Filter 'tab-*.ps1' -ErrorAction SilentlyContinue |
            Sort-Object Name -Descending |
            Select-Object -Skip 5 |
            ForEach-Object { Remove-Item -LiteralPath $_.FullName -Force -ErrorAction SilentlyContinue }

        $wtArgs = New-WtTabArgumentList -PwshPath $pwsh -ScriptPath $tabScript -Title 'DbxProvider'
        Start-Process -FilePath $wt.Source -ArgumentList $wtArgs
        Write-Host "Launched a new Windows Terminal tab running: $script $argLine" -ForegroundColor Cyan
        return
    }
    Write-Warning "Windows Terminal (wt.exe) not found; falling back to a new window."
    $NewWindow = $true
}

if ($NewWindow) {
    Start-Process -FilePath $pwsh -ArgumentList (@('-NoExit') + $baseArgs)
    Write-Host "Launched a new pwsh window running: $script $argLine" -ForegroundColor Cyan
}
else {
    & $pwsh @baseArgs
}
