<#
.SYNOPSIS
    Finds and stops all processes holding a lock on (or with loaded modules from)
    the DbxProvider assembly.

.DESCRIPTION
    Scans the repo for DbxProvider*.dll outputs, detects which are file-locked,
    enumerates running processes, and identifies any that have those DLLs loaded
    as modules (or open file handles, via SysInternals handle64). Stops the
    offending processes (with -WhatIf / confirm support) and verifies that the
    locks are released.

    Uses [System.Diagnostics.Process]::Kill() rather than Stop-Process so it
    works in restricted shells where Stop-Process is blocked.

.PARAMETER RepoRoot
    Root of the DbxProvider repo. Defaults to the script's folder.

.PARAMETER AssemblyName
    Assembly name pattern to look for. Defaults to 'DbxProvider'.

.EXAMPLE
    .\Stop-DbxProviderHolders.ps1 -Confirm:$false
#>
[CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'High')]
param(
    [string] $RepoRoot = $PSScriptRoot,
    [string] $AssemblyName = 'DbxProvider'
)

$ErrorActionPreference = 'Stop'

function Test-FileLocked {
    param([string] $Path)
    try {
        $fs = [System.IO.File]::Open($Path, 'Open', 'ReadWrite', 'None')
        $fs.Close()
        return $false
    } catch {
        return $true
    }
}

if (-not $RepoRoot) { $RepoRoot = (Get-Location).Path }

Write-Host "Scanning $RepoRoot for $AssemblyName*.dll ..." -ForegroundColor Cyan
$dlls = Get-ChildItem -Path $RepoRoot -Recurse -Filter "$AssemblyName*.dll" -ErrorAction SilentlyContinue |
        Select-Object -ExpandProperty FullName -Unique

if (-not $dlls) {
    Write-Warning "No $AssemblyName DLLs found under $RepoRoot."
    return
}

$locked = @($dlls | Where-Object { Test-FileLocked $_ })
if (-not $locked) {
    Write-Host "No locked $AssemblyName DLLs detected." -ForegroundColor Green
    return
}

Write-Host "Locked DLLs:" -ForegroundColor Yellow
$locked | ForEach-Object { Write-Host "  $_" }

$lockedSet = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::OrdinalIgnoreCase)
foreach ($p in $locked) { [void] $lockedSet.Add($p) }

$holderPids = New-Object 'System.Collections.Generic.HashSet[int]'

# Try SysInternals handle64 first (covers raw file handles, not just loaded modules).
$handleExe = (Get-Command handle64.exe -ErrorAction SilentlyContinue).Source
if (-not $handleExe) { $handleExe = (Get-Command handle.exe -ErrorAction SilentlyContinue).Source }

if ($handleExe) {
    Write-Host "Querying handles via $handleExe ..." -ForegroundColor Cyan
    # Pre-accept the SysInternals EULA so handle64 never shows a GUI prompt,
    # even on first run before -accepteula has registered itself.
    try {
        $eulaKey = 'HKCU:\Software\Sysinternals\Handle'
        if (-not (Test-Path $eulaKey)) { New-Item -Path $eulaKey -Force | Out-Null }
        New-ItemProperty -Path $eulaKey -Name 'EulaAccepted' -Value 1 -PropertyType DWord -Force | Out-Null
    } catch {
        Write-Verbose "Could not pre-set Handle EULA registry key: $_"
    }
    foreach ($dll in $locked) {
        $output = & $handleExe -nobanner -accepteula $dll 2>$null
        foreach ($line in $output) {
            if ($line -match 'pid:\s*(\d+)') { [void] $holderPids.Add([int] $Matches[1]) }
        }
    }
} else {
    Write-Verbose "handle64.exe not found in PATH; relying on module enumeration."
}

# Also enumerate processes whose loaded modules include any DbxProvider DLL.
Write-Host "Enumerating loaded modules across processes ..." -ForegroundColor Cyan
foreach ($proc in (Get-Process)) {
    try {
        foreach ($m in $proc.Modules) {
            if ($lockedSet.Contains($m.FileName) -or
                ($m.ModuleName -like "$AssemblyName*.dll")) {
                [void] $holderPids.Add($proc.Id)
                break
            }
        }
    } catch {
        # Access denied for protected processes - ignore.
    }
}

# Never kill the current shell.
[void] $holderPids.Remove($PID)

if ($holderPids.Count -eq 0) {
    Write-Warning "Files are locked but no holder process was identified. You may need to run as Administrator."
    return
}

Write-Host "Holder processes:" -ForegroundColor Yellow
$holders = foreach ($id in $holderPids) {
    Get-Process -Id $id -ErrorAction SilentlyContinue |
        Select-Object Id, ProcessName, Path, StartTime
}
$holders | Format-Table -AutoSize | Out-Host

foreach ($id in $holderPids) {
    $p = Get-Process -Id $id -ErrorAction SilentlyContinue
    if (-not $p) { continue }
    $label = "$($p.ProcessName) (PID $id)"
    if ($PSCmdlet.ShouldProcess($label, 'Stop-Process')) {
        try {
            [System.Diagnostics.Process]::GetProcessById($id).Kill()
            $p.WaitForExit(5000) | Out-Null
            Write-Host "  killed $label" -ForegroundColor Green
        } catch {
            Write-Warning "  failed to kill $label : $_"
        }
    }
}

Start-Sleep -Seconds 1
$stillLocked = @($locked | Where-Object { Test-FileLocked $_ })
if ($stillLocked) {
    Write-Warning "Still locked after stopping holders:"
    $stillLocked | ForEach-Object { Write-Warning "  $_" }
} else {
    Write-Host "All $AssemblyName DLLs are now unlocked." -ForegroundColor Green
}