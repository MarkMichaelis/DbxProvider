#requires -Version 7.4
<#
.SYNOPSIS
    Seed (or augment) the Dropbox conflict-file manifest from a local NAS mirror
    using the Everything CLI (es.exe), then verify each candidate against
    Dropbox so the result is trustworthy.

.DESCRIPTION
    Find-DropboxConflicts.ps1 walks the *authoritative* Dropbox tree, which on a
    large account can take days. When a local NAS mirror of the Dropbox content
    is indexed by Everything (voidtools), this companion script can produce an
    initial candidate list in seconds instead:

        1. Ask Everything (es.exe) for every file under -NasRoot whose name looks
           like a Dropbox conflict file ("...'s conflicted copy...").
        2. Map each NAS path back to its Dropbox drive path (-NasRoot is treated
           as the Dropbox root by default; override with -DropboxPrefix).
        3. Unless -NoVerify is given, confirm each candidate against Dropbox
           (Dropbox is the master): the file must still exist and be zero bytes
           (or any size with -IncludeNonZero). The NAS is only an approximation,
           so verification is what makes the manifest safe to delete from.
        4. Append de-duplicated rows to the SAME CSV manifest that
           Find-DropboxConflicts.ps1 uses, so the two approaches converge on one
           canonical, de-duplicated list.

    This script is READ-ONLY. It never deletes anything. Feed the resulting
    manifest to Remove-DropboxItemBatch (with -WhatIf first) to delete.

    Because the NAS share also contains non-Dropbox folders (backups, #recycle,
    etc.) and may be subject to Dropbox selective sync, treat the NAS list as an
    accelerator / cross-check, not a complete replacement for the authoritative
    walk. Verification discards anything that is not a real zero-byte conflict
    file on Dropbox, and any Dropbox folders missing from the NAS simply will not
    appear here (run the full walk for completeness).

.PARAMETER NasRoot
    UNC/local path to the root of the NAS mirror. Defaults to \\10.0.0.30\Data.
    Everything is queried for conflict files under this path.

.PARAMETER DropboxPrefix
    Dropbox drive path that -NasRoot corresponds to. Defaults to the drive root
    ('Dbx:\'), i.e. \\10.0.0.30\Data\<X> maps to Dbx:\<X>. Set this if the
    Dropbox content lives under a NAS subfolder -- e.g. -NasRoot
    '\\10.0.0.30\Data\Dropbox' -DropboxPrefix 'Dbx:\'.

.PARAMETER OutputCsv
    Path to the shared CSV manifest. Defaults to the same Desktop file that
    Find-DropboxConflicts.ps1 writes, so results merge.

.PARAMETER Pattern
    Filename -like pattern that identifies a conflict file. Defaults to
    "*conflicted copy*". Applied as a post-filter to the Everything results so a
    loose es query can never introduce false rows.

.PARAMETER IncludeNonZero
    Also capture conflict files that are NOT zero bytes. By default only
    zero-byte conflict files are captured (verified against Dropbox).

.PARAMETER NoVerify
    Skip Dropbox verification and write the raw, NAS-derived candidate paths.
    Faster, but the manifest then contains unverified approximations -- always
    re-verify (e.g. with Remove-DropboxItemBatch -WhatIf) before deleting.

.PARAMETER EsTimeoutSec
    Maximum seconds to wait for the scoped es.exe query to return. If Everything
    is still building its index of the NAS share, queries block; on timeout this
    script warns and exits without modifying the manifest. Default 300.

.PARAMETER ProbeTimeoutSec
    Maximum seconds for the fast pre-flight responsiveness check. If Everything
    does not answer a cheap count query within this window, the script bails
    immediately (rather than waiting the full -EsTimeoutSec) with a clear
    "still indexing" message. Default 20.

.PARAMETER EsPath
    Explicit path to es.exe. Defaults to whatever 'es' resolves to on PATH.

.PARAMETER ModulePath
    Path to the DbxProvider module to import for verification. Defaults to the
    Debug build next to this script. Only used unless -NoVerify is specified.

.EXAMPLE
    ./Get-DropboxConflictsFromNas.ps1
    Query the NAS via Everything, verify each hit against Dropbox, and merge
    confirmed zero-byte conflict files into the shared manifest.

.EXAMPLE
    ./Get-DropboxConflictsFromNas.ps1 -NoVerify
    Fast raw candidate dump from the NAS (no Dropbox calls). Verify before delete.

.EXAMPLE
    ./Get-DropboxConflictsFromNas.ps1 -NasRoot '\\10.0.0.30\Data\Dropbox'
    Use when only a NAS subfolder mirrors Dropbox.

.NOTES
    Requires Everything (voidtools) running with the NAS share added as an
    indexed Folder, and es.exe on PATH. If es queries hang/time out, the index
    is still building -- wait and re-run.
#>
[CmdletBinding()]
param(
    [string]$NasRoot = '\\10.0.0.30\Data',
    [string]$DropboxPrefix = 'Dbx:\',
    [string]$OutputCsv = (Join-Path ([Environment]::GetFolderPath('Desktop')) 'Find-DropboxConflicts.csv'),
    [string]$Pattern = '*conflicted copy*',
    [switch]$IncludeNonZero,
    [switch]$NoVerify,
    [int]$EsTimeoutSec = 300,
    [int]$ProbeTimeoutSec = 20,
    [string]$EsPath,
    [string]$ModulePath = (Join-Path $PSScriptRoot 'src\DbxProvider\bin\Debug\net8.0\DbxProvider.psd1')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# --- Locate es.exe ----------------------------------------------------------
if (-not $EsPath) {
    $cmd = Get-Command es -ErrorAction SilentlyContinue
    if ($cmd) { $EsPath = $cmd.Source }
}
if (-not $EsPath -or -not (Test-Path -LiteralPath $EsPath)) {
    throw "Everything CLI (es.exe) not found. Install Everything + es, or pass -EsPath."
}

$driveName = ($DropboxPrefix -split ':', 2)[0]
$dropboxRootPath = $DropboxPrefix.TrimEnd('\', '/')   # e.g. 'Dbx:'

# --- Run es in a background job with a live heartbeat and a hard timeout -----
# es returns all results at once (not streaming), so without a heartbeat the
# console looks frozen while Everything works. While its index of a network
# share is still building, queries block -- hence the timeout + fast pre-check.
function Invoke-EsWithHeartbeat {
    param(
        [string[]]$EsArgs,
        [int]$TimeoutSec,
        [string]$Activity
    )
    $job = Start-Job -ScriptBlock {
        param($exe, $a)
        & $exe @a 2>$null
    } -ArgumentList $EsPath, (, $EsArgs)

    $hb = [System.Diagnostics.Stopwatch]::StartNew()
    while ($job.State -eq 'Running' -and $hb.Elapsed.TotalSeconds -lt $TimeoutSec) {
        $elapsed = [int]$hb.Elapsed.TotalSeconds
        Write-Progress -Activity $Activity `
            -Status ("waiting for Everything... {0}s / {1}s (still indexing the NAS share blocks queries)" -f $elapsed, $TimeoutSec) `
            -PercentComplete ([Math]::Min(99, [int](100.0 * $elapsed / [Math]::Max(1, $TimeoutSec))))
        Wait-Job $job -Timeout 2 | Out-Null
    }
    Write-Progress -Activity $Activity -Completed

    if ($job.State -eq 'Running') {
        Stop-Job $job; Remove-Job $job -Force
        return [pscustomobject]@{ TimedOut = $true; Output = @() }
    }
    $out = @(Receive-Job $job)
    Remove-Job $job -Force
    return [pscustomobject]@{ TimedOut = $false; Output = $out }
}

# --- Pre-flight: is Everything responsive at all? ---------------------------
# A cheap count query that returns promptly proves the IPC is free. If it
# blocks, the NAS folder-index is still building -- bail fast instead of
# hanging for the full -EsTimeoutSec.
Write-Host "Checking Everything responsiveness (up to ${ProbeTimeoutSec}s)..." -ForegroundColor Cyan
$probe = Invoke-EsWithHeartbeat -EsArgs @('-getresultcount', 'conflicted', 'copy') `
    -TimeoutSec $ProbeTimeoutSec -Activity 'Checking Everything responsiveness'
if ($probe.TimedOut) {
    Write-Warning "Everything did not answer within ${ProbeTimeoutSec}s -- it is still building its index of '$NasRoot'."
    Write-Warning "No changes were made to '$OutputCsv'. Wait for indexing to finish (check the Everything status bar) and re-run."
    return
}
Write-Host "Everything is responsive (global 'conflicted copy' count = $($probe.Output -join ' ')). Running the scoped query..." -ForegroundColor Cyan

# --- Query Everything for conflict candidates under the NAS root ------------
# Two AND terms ('conflicted' + 'copy') keep the query simple; the -Pattern
# post-filter below guarantees correctness regardless of what es returns.
Write-Host "Querying Everything under '$NasRoot' (timeout ${EsTimeoutSec}s)..." -ForegroundColor Cyan
$query = Invoke-EsWithHeartbeat -EsArgs @('-path', $NasRoot, 'conflicted', 'copy') `
    -TimeoutSec $EsTimeoutSec -Activity "Querying Everything under $NasRoot"
if ($query.TimedOut) {
    Write-Warning "es.exe did not return within ${EsTimeoutSec}s. Everything is likely still indexing the NAS share."
    Write-Warning "No changes were made to '$OutputCsv'. Wait for indexing to finish and re-run."
    return
}
$candidates = @($query.Output)

# Keep only real conflict-file names (defensive post-filter), drop blanks.
$candidates = @($candidates |
        ForEach-Object { "$_".TrimEnd() } |
        Where-Object { $_ -and ((Split-Path $_ -Leaf) -like $Pattern) })

# Drop NAS-only folders (Synology backup apps, #recycle, @eaDir, *.hbk, ...).
# These are not Dropbox content; without this, -NoVerify would write bogus
# Dropbox paths. The first path segment under -NasRoot is matched against the
# top-level exclusion list; a handful of internal segments are dropped anywhere.
$excludeNasFolder = @(
    'ActiveBackupForOffice365', 'ActiveBackupForGSuite', 'ActiveBackupForGoogleWorkspace',
    '#recycle', '#snapshot', '.sync', '@eaDir'
)
$excludeSegments = @('#recycle', '#snapshot', '@eaDir', '.sync')
function Test-NasExcluded {
    param([string]$NasPath)
    if (-not $NasPath.StartsWith($NasRoot, [System.StringComparison]::OrdinalIgnoreCase)) { return $false }
    $rel = $NasPath.Substring($NasRoot.Length).TrimStart('\', '/')
    $segs = $rel -split '[\\/]'
    if ($segs.Count -eq 0) { return $false }
    if ($excludeNasFolder -contains $segs[0]) { return $true }
    foreach ($s in $segs) {
        if ($excludeSegments -contains $s) { return $true }
        if ($s -like '*.hbk') { return $true }
    }
    return $false
}

$beforeExclude = $candidates.Count
$candidates = @($candidates | Where-Object { -not (Test-NasExcluded $_) })
$excluded = $beforeExclude - $candidates.Count
if ($excluded -gt 0) {
    Write-Host "Excluded $excluded candidate(s) under non-Dropbox NAS folders ($($excludeNasFolder -join ', '), *.hbk)." -ForegroundColor Cyan
}

Write-Host "Everything returned $($candidates.Count) conflict-file candidate(s)." -ForegroundColor Cyan
if ($candidates.Count -eq 0) {
    Write-Warning "Nothing to do. (Index still building, or the NAS holds no matching files.)"
    return
}

# --- Optional: connect for Dropbox verification ----------------------------
if (-not $NoVerify) {
    if (-not (Get-Module DbxProvider)) {
        if (-not (Test-Path -LiteralPath $ModulePath)) {
            throw "DbxProvider module not found at '$ModulePath'. Build it, pass -ModulePath, or use -NoVerify."
        }
        Import-Module $ModulePath -ErrorAction Stop
    }
    if (-not (Get-PSDrive -Name $driveName -ErrorAction SilentlyContinue)) {
        Write-Host "Connecting to Dropbox (drive '$driveName' not mounted)..." -ForegroundColor Cyan
        Connect-Dropbox | Out-Null
    }
}

# --- Load existing manifest(s) for de-dupe; open for append (lock-tolerant) -
# The long-running Find-DropboxConflicts.ps1 may hold the master manifest open
# for writing (FileShare.Read denies a second writer) and even denies plain
# readers that don't opt into shared writing. So we (a) read with
# FileShare.ReadWrite, and (b) if the master is write-locked, append to a
# sidecar and fold it into the master on a later run when the scan has released
# it.
function Read-ManifestDataLines {
    param([string]$Path)
    $lines = [System.Collections.Generic.List[string]]::new()
    if (-not (Test-Path -LiteralPath $Path)) { return $lines }
    $fs = [System.IO.FileStream]::new($Path, [System.IO.FileMode]::Open,
        [System.IO.FileAccess]::Read, [System.IO.FileShare]::ReadWrite)
    try {
        $sr = [System.IO.StreamReader]::new($fs)
        $first = $true
        while ($null -ne ($l = $sr.ReadLine())) {
            if ($first) { $first = $false; continue }   # skip header
            if ($l) { $lines.Add($l) }
        }
        $sr.Dispose()
    }
    finally { $fs.Dispose() }
    return $lines
}

function Get-PathFromRow {
    param([string]$Row)
    $c = $Row.IndexOf(',')
    if ($c -lt 0) { return $null }
    $p = $Row.Substring($c + 1)
    if ($p.Length -ge 2 -and $p[0] -eq '"' -and $p[-1] -eq '"') {
        $p = $p.Substring(1, $p.Length - 2).Replace('""', '"')
    }
    return $p
}

$sidecar = [System.IO.Path]::ChangeExtension($OutputCsv, '.nas.csv')

$masterRows = @(Read-ManifestDataLines -Path $OutputCsv)
$sideRows = @(Read-ManifestDataLines -Path $sidecar)

$masterPaths = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
foreach ($r in $masterRows) { $p = Get-PathFromRow $r; if ($p) { [void]$masterPaths.Add($p) } }

$seen = [System.Collections.Generic.HashSet[string]]::new($masterPaths, [System.StringComparer]::OrdinalIgnoreCase)
foreach ($r in $sideRows) { $p = Get-PathFromRow $r; if ($p) { [void]$seen.Add($p) } }
Write-Host "Loaded $($seen.Count) existing path(s) for de-dupe (master=$($masterPaths.Count), sidecar=$($seen.Count - $masterPaths.Count))." -ForegroundColor Cyan

# Try to append to the master; if the running scan holds it, use the sidecar.
$targetCsv = $OutputCsv
$writer = $null
try {
    $masterExists = Test-Path -LiteralPath $OutputCsv
    $fsw = [System.IO.FileStream]::new($OutputCsv, [System.IO.FileMode]::Append,
        [System.IO.FileAccess]::Write, [System.IO.FileShare]::Read)
    $writer = [System.IO.StreamWriter]::new($fsw, [System.Text.UTF8Encoding]::new($false))
    if (-not $masterExists) { $writer.WriteLine('Bytes,Path') }

    # Master is writable -> fold any prior sidecar rows in, then drop the sidecar.
    if ($sideRows.Count -gt 0) {
        $folded = 0
        foreach ($r in $sideRows) {
            $p = Get-PathFromRow $r
            if ($p -and -not $masterPaths.Contains($p)) { $writer.WriteLine($r); [void]$masterPaths.Add($p); $folded++ }
        }
        $writer.Flush()
        if ($folded -gt 0) { Write-Host "Folded $folded prior sidecar row(s) into the master." -ForegroundColor Cyan }
        Remove-Item -LiteralPath $sidecar -ErrorAction SilentlyContinue
    }
}
catch [System.IO.IOException] {
    if ($writer) { $writer.Dispose() }
    $targetCsv = $sidecar
    Write-Warning "Master manifest is locked (the scan is still writing it):"
    Write-Warning "  $OutputCsv"
    Write-Warning "Writing new rows to a sidecar instead: $sidecar"
    Write-Warning "Re-run this script after the scan finishes to fold the sidecar into the master."
    $sideExists = Test-Path -LiteralPath $sidecar
    $fsw = [System.IO.FileStream]::new($sidecar, [System.IO.FileMode]::Append,
        [System.IO.FileAccess]::Write, [System.IO.FileShare]::Read)
    $writer = [System.IO.StreamWriter]::new($fsw, [System.Text.UTF8Encoding]::new($false))
    if (-not $sideExists) { $writer.WriteLine('Bytes,Path') }
}
$writer.AutoFlush = $true

$added = 0; $verifiedGone = 0; $verifiedNonZero = 0; $examined = 0
$sw = [System.Diagnostics.Stopwatch]::StartNew()

try {
    foreach ($nasPath in $candidates) {
        $examined++

        # Map NAS path -> Dropbox drive path. Require the candidate to live under
        # -NasRoot; anything outside the mirror is ignored.
        if ($nasPath.Length -le $NasRoot.Length -or
            -not $nasPath.StartsWith($NasRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
            continue
        }
        $rel = $nasPath.Substring($NasRoot.Length).TrimStart('\', '/')
        $drivePath = $dropboxRootPath + '\' + ($rel -replace '/', '\')

        if ($NoVerify) {
            # Unverified: record the Dropbox-style (/) path; size unknown (blank).
            $dropboxPath = '/' + ($rel -replace '\\', '/')
            if ($seen.Add($dropboxPath)) {
                $added++
                $writer.WriteLine((',"{0}"' -f ($dropboxPath -replace '"', '""')))
            }
        }
        else {
            # Verified: Dropbox is the master. Use the path/size Dropbox reports.
            $it = Get-Item -LiteralPath $drivePath -ErrorAction SilentlyContinue
            if ($null -eq $it) { $verifiedGone++; continue }
            if (-not $IncludeNonZero -and $it.Length -ne 0) { $verifiedNonZero++; continue }
            if ($seen.Add($it.Path)) {
                $added++
                $writer.WriteLine(('{0},"{1}"' -f $it.Length, ($it.Path -replace '"', '""')))
            }
        }

        if (($examined % 250) -eq 0) {
            Write-Progress -Activity 'Verifying NAS conflict candidates against Dropbox' `
                -Status ("examined={0}/{1} added={2} gone={3} nonzero={4} {5}s" -f `
                    $examined, $candidates.Count, $added, $verifiedGone, $verifiedNonZero, [int]$sw.Elapsed.TotalSeconds) `
                -CurrentOperation $nasPath
        }
    }
}
finally {
    $writer.Flush(); $writer.Dispose()
    Write-Progress -Activity 'Verifying NAS conflict candidates against Dropbox' -Completed
}

Write-Host ''
if ($NoVerify) {
    Write-Host ("Added {0} new (UNVERIFIED) candidate(s) from {1} NAS hit(s) in {2}s." -f `
            $added, $examined, [int]$sw.Elapsed.TotalSeconds) -ForegroundColor Yellow
    Write-Host ('Verify before deleting:  (Import-Csv "{0}").Path | Remove-DropboxItemBatch -WhatIf' -f $targetCsv) -ForegroundColor Gray
}
else {
    Write-Host ("Added {0} new verified row(s). Skipped {1} not-on-Dropbox, {2} non-zero. ({3} NAS hits, {4}s)" -f `
            $added, $verifiedGone, $verifiedNonZero, $examined, [int]$sw.Elapsed.TotalSeconds) -ForegroundColor Green
}
if ($targetCsv -eq $sidecar) {
    Write-Host "Wrote to SIDECAR (master was locked by the running scan): $targetCsv" -ForegroundColor Yellow
    Write-Host "It will be folded into '$OutputCsv' next time you run this after the scan ends." -ForegroundColor Yellow
}
else {
    Write-Host "Manifest (de-duplicated): $targetCsv" -ForegroundColor Green
}
