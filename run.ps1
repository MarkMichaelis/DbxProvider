#Requires -Version 7.0
<#
.SYNOPSIS
    Build DbxProvider fresh and launch a child pwsh with the freshly-built module imported.

.DESCRIPTION
    Your interactive pwsh session loads (and locks) DbxProvider.dll, so it keeps running
    the OLD build even after a rebuild -- and a fixed temp deploy copy can ALSO be locked
    by a previous child run, silently leaving you on stale bits.

    To make stale-core impossible, this script builds the module into a UNIQUE, timestamped
    output directory (which can never be locked by a prior run) and then starts a SEPARATE
    pwsh process that imports that freshly-built module. Console-output / cmdlet / core
    changes therefore always take effect without restarting your main shell.

    By default it drops you into an interactive child pwsh with the fresh module imported
    (no Find-DropboxConflicts run). Pass -FindConflicts to instead run the conflict-delete
    pass:
        Find-DropboxConflicts.ps1 -Delete -SkipScan -Limit <Limit>

.PARAMETER FindConflicts
    Run Find-DropboxConflicts.ps1 -Delete -SkipScan -Limit <Limit> in the freshly-built child
    pwsh instead of opening an interactive session. -ScriptArgs are appended so you can override
    or extend the conflict-script arguments (e.g. -ScriptArgs '-WhatIf').

.PARAMETER Limit
    Value passed to Find-DropboxConflicts.ps1 -Limit (only used with -FindConflicts). Default 100000.

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
    Launch in a separate visible window that stays open (-NoExit). Without this, the default
    interactive session attaches to the current console; a -FindConflicts run attaches and the
    window closes when the pass completes.

.PARAMETER NewTab
    Launch in a new Windows Terminal tab (via wt.exe) instead of a separate window, so the run
    sits alongside the current session. The tab stays open (-NoExit). Falls back to -NewWindow
    (with a warning) when wt.exe is unavailable.

.EXAMPLE
    .\run.ps1
    Builds fresh and opens an interactive child pwsh with the module imported (no deletes).

.EXAMPLE
    .\run.ps1 -NewWindow
    Builds fresh and opens a separate interactive window with the module imported.

.EXAMPLE
    .\run.ps1 -FindConflicts -NewTab
    Builds fresh and opens a new Windows Terminal tab running the conflict-delete pass.

.EXAMPLE
    .\run.ps1 -FindConflicts -Limit 1000 -ScriptArgs '-WhatIf'
    Builds fresh and dry-runs the conflict pass over 1000 items.
#>
[CmdletBinding()]
param(
    [switch]   $FindConflicts,
    [int]      $Limit = 100000,
    [string]   $Configuration = 'Debug',
    [switch]   $NoBuild,
    [string]   $ModulePath,
    [string[]] $ScriptArgs,
    [switch]   $NewWindow,
    [switch]   $NewTab
)

$ErrorActionPreference = 'Stop'

function New-ChildCommand {
    # Builds the command string the freshly-built child pwsh runs. By default it imports the
    # module and stays interactive (no deletes). With -FindConflicts it instead invokes the
    # conflict-delete script (-Delete -Limit, plus any -ScriptArgs the caller appended).
    [CmdletBinding()]
    [OutputType([string])]
    param(
        [Parameter(Mandatory)][string]$RepoRoot,
        [Parameter(Mandatory)][string]$ModulePath,
        [switch]$FindConflicts,
        [string]$ConflictScript,
        [int]$Limit = 100000,
        [string[]]$ScriptArgs
    )

    $prefix = "Set-Location -LiteralPath `"$RepoRoot`""
    if (-not $FindConflicts) {
        $note = "Write-Host 'DbxProvider module imported (fresh build). Run Find-DropboxConflicts.ps1 yourself if you need it.' -ForegroundColor Green"
        return "$prefix; Import-Module `"$ModulePath`" -ErrorAction Stop; $note"
    }

    $invokeArgs = @('-Delete', '-SkipScan', '-Limit', $Limit)
    if ($ScriptArgs) { $invokeArgs += $ScriptArgs }
    $argLine = ($invokeArgs | ForEach-Object {
            if ($_ -is [string] -and $_ -match '\s') { '"{0}"' -f $_ } else { "$_" }
        }) -join ' '
    return "$prefix; & `"$ConflictScript`" -ModulePath `"$ModulePath`" $argLine"
}

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

# Build the command the child pwsh runs: interactive module import by default, or the
# conflict-delete pass when -FindConflicts is supplied (-ScriptArgs appended either way).
$command = New-ChildCommand -RepoRoot $repoRoot -ModulePath $ModulePath `
    -FindConflicts:$FindConflicts -ConflictScript $script -Limit $Limit -ScriptArgs $ScriptArgs
$launchDesc = if ($FindConflicts) { "$script (-Delete -Limit $Limit)" } else { 'an interactive pwsh with DbxProvider imported' }

$pwsh = (Get-Process -Id $PID).Path   # use the same pwsh executable that's running this script
# Default (interactive) sessions keep the child alive with -NoExit; the conflict pass runs and exits.
$baseArgs = if ($FindConflicts) { @('-NoProfile', '-Command', $command) } else { @('-NoProfile', '-NoExit', '-Command', $command) }

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
        Write-Host "Launched a new Windows Terminal tab running: $launchDesc" -ForegroundColor Cyan
        return
    }
    Write-Warning "Windows Terminal (wt.exe) not found; falling back to a new window."
    $NewWindow = $true
}

if ($NewWindow) {
    Start-Process -FilePath $pwsh -ArgumentList (@('-NoExit') + @('-NoProfile', '-Command', $command))
    Write-Host "Launched a new pwsh window running: $launchDesc" -ForegroundColor Cyan
}
else {
    & $pwsh @baseArgs
}
