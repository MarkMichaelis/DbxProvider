#requires -Version 7.4
<#
.SYNOPSIS
    Stream-scan a Dropbox account (via the DbxProvider module) for zero-byte
    "...'s conflicted copy..." files and write them to a CSV manifest.

.DESCRIPTION
    By default this runs the provider's cursor-pruned Find-DropboxConflict
    cmdlet: the first run does a single server-side recursive enumeration
    (streamed page-by-page, so memory stays bounded even on huge accounts) and
    saves a cursor next to the manifest; later runs fetch only the delta since
    that cursor, so repeat scans are far cheaper than re-walking the tree. New
    matches are MERGED (append + de-dupe) into the manifest, never overwriting it.

    Pass -BfsWalk to use the legacy breadth-first walk instead: it lists each
    folder non-recursively (one round-trip per folder), flushing every match to
    the CSV the instant it is found, and supports -Resume of throttled folders.
    It is slower but streams continuously and is resilient to mid-scan
    interruption without relying on a saved cursor.

    The script only READS by default. To delete, re-run with -Delete (which
    supports -WhatIf / -Confirm) or pipe the manifest into Remove-DropboxItemBatch
    yourself -- see the examples.

.PARAMETER OutputCsv
    Path to the CSV manifest. Defaults to Find-DropboxConflicts.csv on the Desktop.

.PARAMETER StartPath
    PowerShell drive path to start from. Defaults to the drive root 'Dbx:\'.
    Use e.g. 'Dbx:\SomeFolder' to scan a single subtree.

.PARAMETER Pattern
    Filename -like pattern that identifies a conflict file. Defaults to
    "*'s conflicted copy*" (straight ASCII apostrophe, as Dropbox writes it).

.PARAMETER IncludeNonZero
    Also capture conflict files that are NOT zero bytes. By default only
    zero-byte conflict files are captured.

.PARAMETER Incremental
    Deprecated / no-op. The cursor-pruned scan is now the DEFAULT, so this switch
    is accepted only for backward compatibility. Use -BfsWalk to opt into the
    legacy breadth-first walk.

.PARAMETER BfsWalk
    Use the legacy breadth-first walk (one non-recursive list_folder call per
    folder) instead of the default cursor-pruned recursive scan. Slower, but
    streams every match to the CSV as it is found and supports -Resume of folders
    that failed to list (e.g. throttling). Implied by -Resume / -RetryFailedLog.

.PARAMETER Full
    Ignore any saved incremental state and force a full recursive pass (which
    then saves fresh state for the next incremental run). Default mode only.

.PARAMETER ModulePath
    Path to the DbxProvider module manifest/DLL to import. Defaults to the
    Debug build next to this script. Adjust if you run the Release build.

.PARAMETER Delete
    After scanning, delete every path in the manifest via Remove-DropboxItemBatch.
    Honors -WhatIf and -Confirm. Off by default.

.EXAMPLE
    ./Find-DropboxConflicts.ps1
    Default cursor-pruned scan from the drive root; merges matches into the
    manifest on the Desktop and saves a cursor for cheap re-runs.

.EXAMPLE
    ./Find-DropboxConflicts.ps1 -StartPath 'Dbx:\IntelliTect.Old(2026-03-01)'
    Scan just one subtree.

.EXAMPLE
    ./Find-DropboxConflicts.ps1 -Delete -WhatIf
    Scan, then preview the deletions without removing anything.

.EXAMPLE
    (Import-Csv "$env:USERPROFILE\Desktop\Find-DropboxConflicts.csv").Path |
        Remove-DropboxItemBatch -WhatIf
    Delete from a previously-saved manifest without re-scanning (preview).

.EXAMPLE
    ./Find-DropboxConflicts.ps1 -BfsWalk -Resume
    Finish an interrupted breadth-first walk: keep the SAME manifest, re-scan
    only the folders recorded in its *.failed-folders.txt (descending into them
    to recover missed matches), and append de-duplicated rows. Repeats until a
    pass completes with zero failures.

.NOTES
    The default scan saves its cursor and current match set next to the manifest
    (*.state.json) periodically, so an interrupted run resumes from where it left
    off on the next invocation instead of restarting. A -BfsWalk run instead
    self-repeats: after the initial walk it automatically re-scans any folders
    that failed (Dropbox throttling, transient errors), pausing between passes,
    until a pass finishes with no failures or only permanent errors remain.
    Either way, everything lands in one de-duplicated manifest.
#>
[CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'High')]
param(
    [string]$OutputCsv = (Join-Path ([Environment]::GetFolderPath('Desktop')) 'Find-DropboxConflicts.csv'),
    [string]$StartPath = 'Dbx:\',
    [string]$Pattern = "*'s conflicted copy*",
    [switch]$IncludeNonZero,
    [string]$ModulePath = (Join-Path $PSScriptRoot 'src\DbxProvider\bin\Debug\net8.0\DbxProvider.psd1'),

    # Resume an interrupted/partial run: keep the existing manifest, load the
    # paths already in it (so nothing is duplicated), and re-scan the folders
    # listed in its *.failed-folders.txt.
    [switch]$Resume,

    # Resume from an explicit failed-folders log instead of the one derived
    # from -OutputCsv. Implies -Resume semantics (append + dedupe).
    [string]$RetryFailedLog,

    # Safety cap on the number of automatic passes. The scan normally stops on
    # its own when a pass completes with zero failures, or when the set of
    # failing folders stops shrinking (only permanent errors remain).
    [int]$MaxPasses = 100,

    # Seconds to pause between passes so Dropbox throttling subsides.
    [int]$RetryPauseSec = 30,

    # Fast path: use the provider's cursor-pruned Find-DropboxConflict cmdlet.
    # The cold run does one full recursive enumeration and saves a cursor; later
    # runs fetch only the delta since that cursor instead of re-walking the tree.
    # This is now the DEFAULT, so -Incremental is accepted but no longer needed.
    [switch]$Incremental,

    # Opt into the legacy breadth-first walk (one list_folder call per folder).
    # Slower (a round-trip per folder) but streams every match to the CSV as it
    # is found and supports -Resume of throttled folders. The default is the
    # cursor-pruned recursive scan via Find-DropboxConflict.
    [switch]$BfsWalk,

    # Ignore any saved incremental state and force a full recursive pass (which
    # then saves fresh state for the next incremental run). Default mode only.
    [switch]$Full,

    [switch]$Delete
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ($RetryFailedLog) { $Resume = $true }
# -Resume / -RetryFailedLog are breadth-first-walk concepts (they re-scan the
# folders recorded in a failed-folders log), so they imply -BfsWalk.
if ($Resume) { $BfsWalk = $true }
if ($MaxPasses -lt 1) { $MaxPasses = 1 }

# --- Ensure the module is loaded and a Dropbox drive is mounted ------------
if (-not (Get-Module DbxProvider)) {
    if (-not (Test-Path -LiteralPath $ModulePath)) {
        throw "DbxProvider module not found at '$ModulePath'. Build it or pass -ModulePath."
    }
    Import-Module $ModulePath -ErrorAction Stop
}

$driveName = ($StartPath -split ':', 2)[0]
if (-not (Get-PSDrive -Name $driveName -ErrorAction SilentlyContinue)) {
    Write-Host "Connecting to Dropbox (drive '$driveName' not mounted)..." -ForegroundColor Cyan
    Connect-Dropbox | Out-Null
}

$failLog = [System.IO.Path]::ChangeExtension($OutputCsv, '.failed-folders.txt')
$failDetail = [System.IO.Path]::ChangeExtension($OutputCsv, '.failed-detail.tsv')

if (-not $BfsWalk) {
    # --- Default: cursor-pruned scan via the provider cmdlet ----------------
    # The first run is a full recursive enumeration that streams page-by-page
    # (bounded memory) and saves a cursor next to the manifest; later runs fetch
    # only the delta. Matches are emitted as objects with Bytes/Path. New rows
    # are MERGED into the existing manifest (append + de-dupe) so an existing
    # walk's results are never clobbered.
    if ($Incremental) {
        Write-Verbose 'The cursor-pruned scan is now the default; -Incremental is redundant.'
    }

    # State lives next to the manifest so it is discoverable and travels with the
    # CSV, and a re-run naturally resumes from the saved cursor.
    $statePath = [System.IO.Path]::ChangeExtension($OutputCsv, '.state.json')

    Write-Host 'Incremental scan via Find-DropboxConflict...' -ForegroundColor Cyan
    $swInc = [System.Diagnostics.Stopwatch]::StartNew()
    $conflicts = @(Find-DropboxConflict -Path $StartPath -Pattern $Pattern -DriveName $driveName `
            -IncludeNonZero:$IncludeNonZero -Full:$Full -StatePath $statePath)

    # Merge (append + de-dupe) into the existing manifest instead of overwriting
    # it, so results from a prior walk / NAS seed are preserved.
    $seen = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
    if (Test-Path -LiteralPath $OutputCsv) {
        foreach ($row in Import-Csv -LiteralPath $OutputCsv) { [void]$seen.Add($row.Path) }
        Write-Host "Loaded $($seen.Count) existing match(es) from $OutputCsv for de-dupe." -ForegroundColor Cyan
    }

    $append = Test-Path -LiteralPath $OutputCsv
    $writer = [System.IO.StreamWriter]::new($OutputCsv, $append, [System.Text.UTF8Encoding]::new($false))
    if (-not $append) { $writer.WriteLine('Bytes,Path') }
    $added = 0
    try {
        foreach ($m in $conflicts) {
            if ($seen.Add($m.Path)) {
                $added++
                $writer.WriteLine(('{0},"{1}"' -f $m.Bytes, ($m.Path -replace '"', '""')))
            }
        }
    }
    finally { $writer.Flush(); $writer.Dispose() }

    Write-Host ("Incremental scan complete in {0}s. matched={1} (added {2} new). Manifest: {3}" -f `
            [int]$swInc.Elapsed.TotalSeconds, $conflicts.Count, $added, $OutputCsv) -ForegroundColor Green
    Write-Host "Scan state (for the next delta run): $statePath" -ForegroundColor Gray
}
else {
# --- Determine seeds + resume/dedupe state ---------------------------------
# $seen holds every match path already written, so retried subtrees and
# resumed runs never duplicate a row in the single canonical manifest.
$seen = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)

if ($Resume) {
    $seedLog = if ($RetryFailedLog) { $RetryFailedLog } else { $failLog }
    if (-not (Test-Path -LiteralPath $seedLog)) {
        throw "Resume requested but no failed-folders log found at '$seedLog'."
    }
    $seeds = @(Get-Content -LiteralPath $seedLog |
        ForEach-Object { $_.Trim() } | Where-Object { $_ } | Select-Object -Unique)
    Write-Host "Resume mode: $($seeds.Count) failed folder(s) from $seedLog" -ForegroundColor Cyan

    # Preload existing matches so appended rows are de-duplicated.
    if (Test-Path -LiteralPath $OutputCsv) {
        foreach ($row in Import-Csv -LiteralPath $OutputCsv) { [void]$seen.Add($row.Path) }
        Write-Host "Loaded $($seen.Count) existing match(es) from $OutputCsv for de-dupe." -ForegroundColor Cyan
    }
}
else {
    $seeds = @($StartPath)
}

# Append when resuming an existing manifest; otherwise start a fresh file.
$append = $Resume -and (Test-Path -LiteralPath $OutputCsv)
if (-not $append) { Remove-Item -LiteralPath $failLog, $failDetail -ErrorAction SilentlyContinue }
$writer = [System.IO.StreamWriter]::new($OutputCsv, $append, [System.Text.UTF8Encoding]::new($false))
if (-not $append) { $writer.WriteLine('Bytes,Path') }
$writer.AutoFlush = $true

$folders = 0; $scanned = 0; $matched = 0
$sw = [System.Diagnostics.Stopwatch]::StartNew()
$pass = 0
$prevFailKey = $null
$stillFailed = 0
$complete = ($seeds.Count -eq 0)   # nothing to do => already complete

try {
    while ($seeds.Count -gt 0 -and $pass -lt $MaxPasses) {
        $pass++
        $failedThisPass = [System.Collections.Generic.List[string]]::new()
        # Rewrite the residual logs each pass so they reflect only what is
        # still failing right now.
        Remove-Item -LiteralPath $failLog, $failDetail -ErrorAction SilentlyContinue

        $queue = [System.Collections.Generic.Queue[string]]::new()
        foreach ($s in $seeds) { $queue.Enqueue($s) }

        while ($queue.Count -gt 0) {
            $dir = $queue.Dequeue()
            $folders++
            try {
                foreach ($it in Get-ChildItem -LiteralPath $dir -ErrorAction Stop) {
                    $scanned++
                    if ($it.IsFolder) {
                        # Descend using a drive-qualified path; Path -> drive
                        # path is unambiguous.
                        $queue.Enqueue("${driveName}:" + ($it.Path -replace '/', '\'))
                    }
                    elseif ($it.Name -like $Pattern -and ($IncludeNonZero -or $it.Length -eq 0)) {
                        # De-dupe: only write a path we have not seen before.
                        if ($seen.Add($it.Path)) {
                            $matched++
                            # CSV-quote the path (names can contain commas/quotes).
                            $writer.WriteLine(('{0},"{1}"' -f $it.Length, ($it.Path -replace '"', '""')))
                        }
                    }
                }
            }
            catch {
                # A folder that fails (throttle exhaustion, transient error)
                # must not abort the scan. Record it (path + reason); it will be
                # retried in the next pass.
                $failedThisPass.Add($dir)
                Add-Content -LiteralPath $failLog -Value $dir
                Add-Content -LiteralPath $failDetail -Value ("{0}`t{1}" -f $dir, ($_.Exception.Message -replace '\s+', ' '))
            }

            Write-Progress -Activity 'Scanning Dropbox for conflict files' `
                -Status ("pass={0} folders={1} scanned={2} matched={3} failed-now={4} queue={5} {6}s" -f `
                    $pass, $folders, $scanned, $matched, $failedThisPass.Count, $queue.Count, [int]$sw.Elapsed.TotalSeconds) `
                -CurrentOperation $dir
        }

        $stillFailed = $failedThisPass.Count
        if ($stillFailed -eq 0) { $complete = $true; break }

        # Converged? If the same set of folders fails two passes running, the
        # remaining errors are permanent -- stop instead of looping forever.
        $failKey = (($failedThisPass | Sort-Object -Unique) -join "`n")
        if ($failKey -eq $prevFailKey) {
            Write-Host ("Pass {0}: the same {1} folder(s) keep failing -- treating as permanent. Stopping." -f `
                    $pass, $stillFailed) -ForegroundColor Yellow
            break
        }
        $prevFailKey = $failKey

        # Re-seed the next pass from the folders that still failed.
        $seeds = $failedThisPass.ToArray()
        if ($pass -lt $MaxPasses) {
            Write-Host ("Pass {0} complete: {1} folder(s) still failing. Pausing {2}s before pass {3}..." -f `
                    $pass, $stillFailed, $RetryPauseSec, ($pass + 1)) -ForegroundColor Yellow
            Start-Sleep -Seconds $RetryPauseSec
        }
    }
}
finally {
    $writer.Flush(); $writer.Dispose()
    Write-Progress -Activity 'Scanning Dropbox for conflict files' -Completed
    Write-Host ''
    if ($complete) {
        Write-Host ("COMPLETE -- no failures. {0}s over {1} pass(es). matched={2} (folders={3} scanned={4})" -f `
                [int]$sw.Elapsed.TotalSeconds, $pass, $matched, $folders, $scanned) -ForegroundColor Green
    }
    else {
        Write-Host ("Stopped after {0}s / {1} pass(es). matched={2} (folders={3} scanned={4})" -f `
                [int]$sw.Elapsed.TotalSeconds, $pass, $matched, $folders, $scanned) -ForegroundColor Yellow
    }
    Write-Host "Manifest (de-duplicated): $OutputCsv" -ForegroundColor Green
    if ($stillFailed -gt 0 -and (Test-Path -LiteralPath $failLog)) {
        Write-Host "Folders still failing: $stillFailed  (paths: $failLog, reasons: $failDetail)" -ForegroundColor Yellow
        Write-Host "Resume later with:  .\Find-DropboxConflicts.ps1 -Resume" -ForegroundColor Yellow
    }
}
}

# --- Optional deletion ------------------------------------------------------
if ($Delete) {
    $paths = @((Import-Csv -LiteralPath $OutputCsv).Path)
    if ($paths.Count -eq 0) {
        Write-Host 'Nothing to delete (manifest is empty).' -ForegroundColor Yellow
        return
    }
    Write-Host "Deleting $($paths.Count) file(s) from the manifest..." -ForegroundColor Cyan
    # Forward -WhatIf / -Confirm through to Remove-DropboxItemBatch (which is
    # itself SupportsShouldProcess) so this script's preview switches apply.
    $forward = @{}
    if ($PSBoundParameters.ContainsKey('WhatIf'))  { $forward['WhatIf']  = $PSBoundParameters['WhatIf'] }
    if ($PSBoundParameters.ContainsKey('Confirm')) { $forward['Confirm'] = $PSBoundParameters['Confirm'] }
    $paths | Remove-DropboxItemBatch @forward -ErrorAction Continue
}
else {
    Write-Host ''
    Write-Host 'To delete the captured files later, preview then run:' -ForegroundColor Cyan
    Write-Host "  (Import-Csv `"$OutputCsv`").Path | Remove-DropboxItemBatch -WhatIf" -ForegroundColor Gray
    Write-Host "  (Import-Csv `"$OutputCsv`").Path | Remove-DropboxItemBatch" -ForegroundColor Gray
}
