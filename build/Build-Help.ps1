#requires -Version 7.4
<#
.SYNOPSIS
    Compile PlatyPS markdown into the DbxProvider MAML help file and
    enforce help completeness.

.DESCRIPTION
    Authoring source of truth: docs\help\en-US\*.md (PlatyPS markdown).
    Build artifact: <ModuleOutputDir>\en-US\DbxProvider.dll-Help.xml,
    which PowerShell's Get-Help engine discovers automatically next to
    DbxProvider.dll.

    Modes:
      (default)   Validate help, run the completeness gate, emit MAML.
                  Non-zero exit on any gate failure.
      -Update     Run platyPS Update-MarkdownHelp against the built
                  module to refresh parameter blocks for added/renamed
                  parameters without clobbering authored prose, then
                  validate + compile.
      -Scaffold   First-time bootstrap: run New-MarkdownHelp to create
                  one .md per exported cmdlet plus the module landing
                  page. Will not overwrite existing files unless -Force.

.PARAMETER Configuration
    dotnet build configuration whose output to target. Defaults to Release.

.PARAMETER Update
    Refresh parameter blocks from the built assembly.

.PARAMETER Scaffold
    Create missing markdown stubs from the built assembly.

.PARAMETER Force
    With -Scaffold, overwrite existing markdown files.
#>
[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [switch]$Update,
    [switch]$Scaffold,
    [switch]$Force
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot   = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$moduleDir  = Join-Path $repoRoot "src\DbxProvider\bin\$Configuration\net10.0"
$moduleDll  = Join-Path $moduleDir 'DbxProvider.dll'
$psdPath    = Join-Path $repoRoot 'src\DbxProvider\DbxProvider.psd1'
$helpRoot   = Join-Path $repoRoot 'docs\help\en-US'
$mamlOutDir = Join-Path $moduleDir 'en-US'

$moduleName = 'DbxProvider'

function Write-Section($msg) { Write-Host "==> $msg" -ForegroundColor Cyan }
function Write-GateError($msg) {
    Write-Host "##[error]$msg" -ForegroundColor Red
    $script:GateErrors += $msg
}

if (-not (Test-Path -LiteralPath $moduleDll)) {
    throw "Module DLL not found at '$moduleDll'. Build the project first (dotnet build -c $Configuration)."
}

# ---- Ensure platyPS ---------------------------------------------------------
$platy = Get-Module -ListAvailable -Name platyPS |
    Sort-Object Version -Descending | Select-Object -First 1
if (-not $platy) {
    Write-Section 'Installing platyPS (CurrentUser)'
    Install-Module platyPS -Scope CurrentUser -Force -SkipPublisherCheck -AllowClobber
}
Import-Module platyPS -ErrorAction Stop

# ---- Discover exported cmdlets ---------------------------------------------
$psdData    = Import-PowerShellDataFile -LiteralPath $psdPath
$exported   = @($psdData.CmdletsToExport) | Where-Object { $_ -and $_ -ne '*' }
if (-not $exported) {
    throw "No CmdletsToExport found in $psdPath."
}
Write-Section "Exported cmdlets: $($exported.Count)"

# A PowerShell binary module can only be imported by a host running the same
# (or newer) .NET major version it was built for. When the module targets a
# newer runtime than the current host (e.g. a net10.0 module under PowerShell
# 7.4, which runs on .NET 8), importing it fails with a System.Runtime load
# error. Skip help generation with a clear warning rather than failing the
# whole build; a matching PowerShell (7.6+ for net10.0) regenerates the help.
$requiredMajor = if ($moduleDir -match 'net(\d+)\.0') { [int]$Matches[1] } else { 0 }
$hostMajor     = [System.Environment]::Version.Major
if ($requiredMajor -gt $hostMajor) {
    Write-Warning "Skipping help generation: module targets net$requiredMajor.0 but this PowerShell host runs on .NET $hostMajor. Run on a PowerShell built for .NET $requiredMajor (PowerShell 7.6+ for net10.0) to build help."
    return
}

# Import the freshly-built module into a child runspace-equivalent (this
# session) so platyPS can reflect on it.
Remove-Module $moduleName -Force -ErrorAction SilentlyContinue
Import-Module $moduleDll -Force -ErrorAction Stop

# ---- Scaffold mode ---------------------------------------------------------
if ($Scaffold) {
    if (-not (Test-Path -LiteralPath $helpRoot)) {
        New-Item -ItemType Directory -Path $helpRoot -Force | Out-Null
    }
    Write-Section "Scaffolding markdown stubs into $helpRoot"
    $newMdParams = @{
        Module                = $moduleName
        OutputFolder          = $helpRoot
        WithModulePage        = $true
        AlphabeticParamsOrder = $true
        Encoding              = [System.Text.UTF8Encoding]::new($false)
        Locale                = 'en-US'
        HelpVersion           = $psdData.ModuleVersion
    }
    if ($Force) { $newMdParams.Force = $true }
    New-MarkdownHelp @newMdParams | Out-Null
}

# ---- Update mode -----------------------------------------------------------
if ($Update) {
    if (-not (Get-ChildItem -LiteralPath $helpRoot -Filter '*.md' -ErrorAction SilentlyContinue)) {
        throw "No markdown found under '$helpRoot'. Run with -Scaffold first."
    }
    Write-Section "Refreshing parameter blocks (Update-MarkdownHelp)"
    Update-MarkdownHelp -Path $helpRoot -AlphabeticParamsOrder | Out-Null
}

# ---- Completeness gate -----------------------------------------------------
$script:GateErrors = @()

if (-not (Test-Path -LiteralPath $helpRoot)) {
    Write-GateError "Help folder '$helpRoot' does not exist. Run: pwsh build\Build-Help.ps1 -Scaffold"
} else {
    $mdFiles      = Get-ChildItem -LiteralPath $helpRoot -Filter '*.md' -File
    $cmdletMdMap  = @{}
    foreach ($f in $mdFiles) { $cmdletMdMap[$f.BaseName] = $f }

    # 1. Every exported cmdlet has a markdown file.
    foreach ($name in $exported) {
        if (-not $cmdletMdMap.ContainsKey($name)) {
            Write-GateError "Missing help markdown: docs\help\en-US\$name.md (cmdlet '$name' is exported but undocumented)."
        }
    }

    # 6. No orphan markdown (excluding module landing page).
    foreach ($f in $mdFiles) {
        if ($f.BaseName -eq $moduleName) { continue }
        if ($exported -notcontains $f.BaseName) {
            Write-GateError "Orphan help markdown: $($f.Name) (no matching exported cmdlet)."
        }
    }

    # 2-5. Per-cmdlet content checks.
    $placeholderRegex = '\{\{\s*Fill .*?\}\}'
    foreach ($name in $exported) {
        if (-not $cmdletMdMap.ContainsKey($name)) { continue }
        $md = Get-Content -LiteralPath $cmdletMdMap[$name].FullName -Raw

        # Synopsis
        $syn = [regex]::Match($md, '(?ms)^##\s+SYNOPSIS\s*\r?\n(.*?)(?=^##\s)')
        if (-not $syn.Success -or [string]::IsNullOrWhiteSpace($syn.Groups[1].Value) -or
            $syn.Groups[1].Value -match $placeholderRegex) {
            Write-GateError "[$name] SYNOPSIS is empty or contains a placeholder."
        }

        # Description
        $desc = [regex]::Match($md, '(?ms)^##\s+DESCRIPTION\s*\r?\n(.*?)(?=^##\s)')
        if (-not $desc.Success -or [string]::IsNullOrWhiteSpace($desc.Groups[1].Value) -or
            $desc.Groups[1].Value -match $placeholderRegex) {
            Write-GateError "[$name] DESCRIPTION is empty or contains a placeholder."
        }

        # At least one example with a fenced code block.
        $exMatches = [regex]::Matches($md, '(?ms)^###\s+Example.*?\r?\n(.*?)(?=^###\s|^##\s|\z)')
        $hasGoodExample = $false
        foreach ($em in $exMatches) {
            $body = $em.Groups[1].Value
            if ($body -match '```' -and $body -notmatch $placeholderRegex) {
                $hasGoodExample = $true; break
            }
        }
        if (-not $hasGoodExample) {
            Write-GateError "[$name] At least one '### Example' with a fenced code block is required."
        }

        # Parameter descriptions.
        $cmdInfo = Get-Command -Module $moduleName -Name $name -ErrorAction SilentlyContinue
        if ($cmdInfo) {
            $commonParams = @(
                'Verbose','Debug','ErrorAction','WarningAction','InformationAction',
                'ProgressAction','ErrorVariable','WarningVariable','InformationVariable',
                'OutVariable','OutBuffer','PipelineVariable','WhatIf','Confirm'
            )
            $declared = $cmdInfo.Parameters.Keys |
                Where-Object { $commonParams -notcontains $_ }

            foreach ($p in $declared) {
                $pat = "(?ms)^###\s+-$([regex]::Escape($p))\s*\r?\n(.*?)(?=^###\s|^##\s|\z)"
                $pm  = [regex]::Match($md, $pat)
                if (-not $pm.Success) {
                    Write-GateError "[$name] Parameter '-$p' is missing from markdown."
                    continue
                }
                $body = $pm.Groups[1].Value
                # Strip the YAML metadata block (```yaml ... ```) and check what remains.
                $prose = [regex]::Replace($body, '(?ms)```yaml.*?```', '').Trim()
                if ([string]::IsNullOrWhiteSpace($prose) -or $prose -match $placeholderRegex) {
                    Write-GateError "[$name] Parameter '-$p' has no description (or placeholder remains)."
                }
            }
        }
    }
}

if ($GateErrors.Count -gt 0) {
    Write-Host ""
    Write-Host "Help completeness gate FAILED with $($GateErrors.Count) error(s)." -ForegroundColor Red
    exit 1
}
Write-Section 'Help completeness gate passed.'

# ---- Compile MAML ----------------------------------------------------------
if (-not (Test-Path -LiteralPath $mamlOutDir)) {
    New-Item -ItemType Directory -Path $mamlOutDir -Force | Out-Null
}
Write-Section "Compiling MAML to $mamlOutDir"
New-ExternalHelp -Path $helpRoot -OutputPath $mamlOutDir -Force | Out-Null

$produced = Get-ChildItem -LiteralPath $mamlOutDir -Filter '*-Help.xml' -File
foreach ($p in $produced) {
    Write-Host "    $($p.Name) ($([math]::Round($p.Length/1KB,1)) KB)"
}

Write-Host ""
Write-Host 'Help build succeeded.' -ForegroundColor Green
exit 0
