#requires -Version 7.4
<#
.SYNOPSIS
    Find zero-byte "...'s conflicted copy..." files in a Dropbox account (via the
    DbxProvider module) and write them to a CSV manifest.

.DESCRIPTION
    Runs the provider's Find-DropboxConflict cmdlet, which reads conflict files
    straight from the local metadata cache -- there is NO recursive Dropbox
    enumeration, so even a huge account is scanned in seconds. Before reading,
    the cmdlet auto-refreshes the cache from the account delta cursor (draining
    changes since the last sync), so results are current.

    The cache must exist first. Build or refresh it with Build-DropboxCacheAll.ps1
    (a first build of a large account is the slow part; after that, finds are
    cheap and repeatable).

    Matches are MERGED (append + de-dupe) into the CSV manifest, so a prior scan's
    results -- or a seed from another tool -- are never clobbered.

    The script only READS by default. Add -Delete to scan and then delete every
    path in the manifest. Deletion is BATCHED (1000 per API call), RESUMABLE, and
    shows a live countdown: progress is checkpointed to a small JSON file (under
    %TEMP%\DbxProvider by default) after every batch, so if the session dies you
    can re-run with -Delete and it picks up exactly where it left off. Use
    -WhatIf to preview without deleting.

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

.PARAMETER ModulePath
    Path to the DbxProvider module manifest/DLL to import. Defaults to the
    Debug build next to this script. Adjust if you run the Release build.

.PARAMETER Delete
    After scanning, delete every path in the manifest in resumable batches with
    a live countdown. Honors -WhatIf. Off by default.

.PARAMETER SkipScan
    With -Delete, skip the scan and delete straight from the existing manifest
    (e.g. to resume a delete without re-scanning the account).

.PARAMETER BatchSize
    Paths per delete_batch API call. Defaults to 250. Smaller batches finish (and
    so advance the progress bar) more often, keeping progress visible during the
    server-side wait. Independent of -MaxConcurrency, so shrinking it does not add
    write contention. Each progress checkpoint covers one wave (BatchSize *
    MaxConcurrency).

.PARAMETER MaxConcurrency
    How many delete_batch jobs run at once. Defaults to 4. This is the main
    throughput lever, but overlapping writes are also what cause the transient
    too_many_write_operations lock errors, so raising it trades more contention
    for more parallelism. Lower it (e.g. 1 for serial) to eliminate the warning
    flood at a modest throughput cost.

.PARAMETER ProgressDirectory
    Folder for the resumable progress file and the failed-items CSV. Defaults to
    %TEMP%\DbxProvider. Files are named after the manifest (tagged with this
    script's name when the manifest is not already), e.g.
    Find-DropboxConflicts.csv.progress.json.

.PARAMETER ProgressPath
    Full path of the progress file. Overrides ProgressDirectory when set.

.PARAMETER ShowItems
    With -Delete, also print every path as it is deleted (the per-batch
    countdown line is always shown).

.PARAMETER Limit
    With -Delete, process at most this many items in THIS run, then stop.
    Because progress is resumable, this lets you ramp up safely: delete the
    first 10, verify, then 100, then 1000, then the rest. 0 (default) = no cap.
    Aliased as -First.

.PARAMETER ResetProgress
    With -Delete, ignore any saved progress and delete from the top.

.PARAMETER ListDeleted
    Print the files that have already been deleted (the first <Done> rows of the
    manifest, since deletion runs strictly top-to-bottom). Reads only the manifest
    and the progress JSON -- no API call, no Dropbox connection. Combine with
    -Tail to see just the most recently deleted entries.

.PARAMETER Tail
    With -ListDeleted, show only the last N already-deleted paths. 0 = show all.

.EXAMPLE
    ./Find-DropboxConflicts.ps1
    Cache-backed scan from the drive root; merges matches into the manifest on
    the Desktop.

.EXAMPLE
    ./Find-DropboxConflicts.ps1 -StartPath 'Dbx:\IntelliTect.Old(2026-03-01)'
    Scan just one subtree.

.EXAMPLE
    ./Find-DropboxConflicts.ps1 -Delete -WhatIf
    Scan, then preview the deletions without removing anything.

.EXAMPLE
    ./Find-DropboxConflicts.ps1 -Delete
    Scan, then delete with a live, session-surviving countdown.

.EXAMPLE
    ./Find-DropboxConflicts.ps1 -Delete -SkipScan
    Resume deleting from the existing manifest without re-scanning.

.EXAMPLE
    ./Find-DropboxConflicts.ps1 -Delete -SkipScan -Limit 10
    ./Find-DropboxConflicts.ps1 -Delete -SkipScan -Limit 100
    ./Find-DropboxConflicts.ps1 -Delete -SkipScan -Limit 1000
    ./Find-DropboxConflicts.ps1 -Delete -SkipScan
    Staged canary: delete the first 10, verify, then 100, then 1000, then the
    rest. Each run resumes where the last left off (progress survives sessions).

.EXAMPLE
    ./Find-DropboxConflicts.ps1 -ListDeleted
    Show every file deleted so far (and the running done/remaining counts).

.EXAMPLE
    ./Find-DropboxConflicts.ps1 -ListDeleted -Tail 20
    Show just the 20 most recently deleted files.

.NOTES
    Reads the local metadata cache only; it does not walk the tree via the API.
    The cache is auto-refreshed (delta drain) on each run. Populate or rebuild it
    with Build-DropboxCacheAll.ps1 (use -Rebuild if Dropbox rejects the cursor).
    Everything lands in one de-duplicated manifest. Deletion is safe to stop at
    any time (Ctrl+C) and resume -- the counter only advances after a batch is
    actually deleted, so nothing is double-deleted.

    During -Delete, a 'path not found' result is counted as "already gone"
    (benign): the conflict file is already deleted, commonly because its
    conflicted-copy parent folder was removed earlier in the same run. Only
    genuine errors are written to the '<manifest>.failed.csv' sidecar (Path,
    Reason) AND surfaced as warnings at the console, so real failures are never
    silent. Pipe the sidecar back into Remove-DropboxItemBatch to retry.

    Deletes run several delete_batch jobs at once (-MaxConcurrency, default 4).
    Because each batch is server-side async, overlapping jobs is the main
    throughput lever -- but it is also what causes the transient
    too_many_write_operations contention, which retries automatically. Use a
    small -BatchSize (default 250) for frequent, visible progress, and lower
    -MaxConcurrency to reduce contention. Rate-limit (429) backoff is automatic.
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [string]$OutputCsv = (Join-Path ([Environment]::GetFolderPath('Desktop')) 'Find-DropboxConflicts.csv'),
    [string]$StartPath = 'Dbx:\',
    [string]$Pattern = "*'s conflicted copy*",
    [switch]$IncludeNonZero,
    [string]$ModulePath = (Join-Path $PSScriptRoot 'src\DbxProvider\bin\Debug\net8.0\DbxProvider.psd1'),
    [switch]$Delete,
    [switch]$SkipScan,
    [ValidateRange(1, 1000)]
    [int]$BatchSize = 250,
    [ValidateRange(1, 32)]
    [int]$MaxConcurrency = 4,
    [ValidateNotNullOrEmpty()]
    [string]$ProgressDirectory = (Join-Path $env:TEMP 'DbxProvider'),
    [string]$ProgressPath,
    [switch]$ShowItems,
    [ValidateRange(0, [int]::MaxValue)]
    [Alias('First')]
    [int]$Limit = 0,
    [switch]$ListDeleted,
    [ValidateRange(0, [int]::MaxValue)]
    [int]$Tail = 0,
    [switch]$ResetProgress
)

function Resolve-DbxDriveName {
    # Returns the PowerShell drive name from a path's LEADING drive qualifier
    # (e.g. 'Dbx:\Folder' -> 'Dbx'). A pure Dropbox path such as '/A/B', or a
    # colon that appears after a path separator (e.g. '/Project:Notes'), has no
    # qualifier, so the default 'Dbx' drive is returned. Mirrors the provider's
    # StripDrivePrefix so -Path and -DriveName stay consistent.
    [CmdletBinding()]
    param([Parameter(Mandatory)][AllowEmptyString()][string]$Path)

    $colon = $Path.IndexOf(':')
    if ($colon -gt 0) {
        $separator = $Path.IndexOfAny([char[]]('/', '\'))
        if ($separator -lt 0 -or $separator -gt $colon) {
            return $Path.Substring(0, $colon)
        }
    }
    return 'Dbx'
}

function Get-CsvPathField {
    # Parses one manifest line ('Bytes,"Path"') and returns the unescaped Path.
    # Paths never contain newlines, so a single-line parse is sufficient.
    [CmdletBinding()]
    [OutputType([string])]
    param([Parameter(Mandatory)][AllowEmptyString()][string]$Line)

    $comma = $Line.IndexOf(',')
    if ($comma -lt 0) { return '' }
    $field = $Line.Substring($comma + 1).Trim()
    if ($field.StartsWith('"') -and $field.EndsWith('"') -and $field.Length -ge 2) {
        $field = $field.Substring(1, $field.Length - 2).Replace('""', '"')
    }
    return $field
}

function Format-Duration {
    # Renders a TimeSpan as a compact 'Xh Ym' / 'Ym Zs' / 'Zs' string.
    [CmdletBinding()]
    [OutputType([string])]
    param([Parameter(Mandatory)][TimeSpan]$Span)

    if ($Span.TotalHours -ge 1) { return ('{0}h {1:00}m' -f [int]$Span.TotalHours, $Span.Minutes) }
    if ($Span.TotalMinutes -ge 1) { return ('{0}m {1:00}s' -f [int]$Span.TotalMinutes, $Span.Seconds) }
    return ('{0}s' -f [int]$Span.TotalSeconds)
}

function Get-StatusWidth {
    # Usable console width for a single-line status. We render to width-1 so the
    # cursor never advances onto a wrapped second row (which would defeat the
    # carriage-return overwrite). Falls back to 120 when there is no real console
    # (redirected/host without a window) or the width reads as 0.
    [CmdletBinding()]
    [OutputType([int])]
    param()
    try {
        $w = [Console]::WindowWidth
        if ($w -gt 1) { return $w }
    }
    catch { }
    return 120
}

# Module-level state for the in-place status line so every writer (main loop,
# wave ticker, warning finalizer) overwrites the same single row cleanly.
$script:StatusLastLen = 0

function Write-StatusLine {
    # Renders a single status line in place: truncates to the console width so it
    # never wraps, pads to the previously-rendered length to erase any leftover
    # tail, and writes with a leading carriage return via [Console]::Write so it is
    # safe to call from a background timer thread (Console writes are synchronized).
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][AllowEmptyString()][string]$Text,
        [ConsoleColor]$Color = [ConsoleColor]::Green
    )
    $max = (Get-StatusWidth) - 1
    if ($Text.Length -gt $max) { $Text = $Text.Substring(0, $max) }
    $pad = if ($Text.Length -lt $script:StatusLastLen) { $script:StatusLastLen - $Text.Length } else { 0 }
    $prev = [Console]::ForegroundColor
    try {
        [Console]::ForegroundColor = $Color
        [Console]::Write("`r" + $Text + (' ' * $pad))
    }
    finally { [Console]::ForegroundColor = $prev }
    $script:StatusLastLen = $Text.Length
}

function Show-Status {
    # Render an exact status line through whichever in-place writer is active:
    # the C# ticker (preferred, thread-safe), the PowerShell fallback writer, or a
    # plain newline-terminated Write-Host when in-place rendering is unavailable.
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][AllowEmptyString()][string]$Text,
        [ConsoleColor]$Color = [ConsoleColor]::Green
    )
    if ($script:Ticker) { $script:Ticker.Write($Text, $Color) }
    elseif ($script:InplaceOk) { Write-StatusLine -Text $Text -Color $Color }
    else { Write-Host $Text -ForegroundColor $Color }
}

function Stop-StatusLine {
    # Terminate any pending in-place line with a newline so the next output starts
    # on its own row. No-op when nothing is pending or output is plain-line mode.
    [CmdletBinding()]
    param()
    if ($script:Ticker) { $script:Ticker.EndLine() }
    elseif ($script:InplaceOk -and $script:StatusLastLen -gt 0) { [Console]::Write("`n"); $script:StatusLastLen = 0 }
}

function Split-DeleteError {
    # Parses a Remove-DropboxItemBatch ErrorRecord into Path + Reason, and flags
    # the benign 'path not found' case (the conflict file is already gone -- often
    # because its conflicted-copy parent folder was deleted earlier in the run).
    [CmdletBinding()]
    [OutputType([pscustomobject])]
    param([Parameter(Mandatory)]$ErrorRecord)

    $path = [string]$ErrorRecord.TargetObject
    $msg = [string]$ErrorRecord.Exception.Message
    $reason = $msg
    $prefix = "Could not delete '$path': "
    if ($msg.StartsWith($prefix, [System.StringComparison]::Ordinal)) {
        $reason = $msg.Substring($prefix.Length)
    }
    [pscustomobject]@{
        Path   = $path
        Reason = $reason
        IsGone = [bool]($reason -match '(?i)path not found|not_found')
    }
}

function Measure-FileLine {
    # Counts data rows in the manifest (total lines minus the header).
    [CmdletBinding()]
    [OutputType([int])]
    param([Parameter(Mandatory)][string]$Path)

    $count = 0
    $reader = [System.IO.StreamReader]::new($Path)
    try { while ($null -ne $reader.ReadLine()) { $count++ } }
    finally { $reader.Dispose() }
    return [Math]::Max(0, $count - 1)
}

function Save-Progress {
    # Atomically persists the resumable delete counter to a JSON sidecar.
    [CmdletBinding(SupportsShouldProcess)]
    param(
        [int]$Done, [int]$Total, [int]$Failed, [int]$Gone,
        [string]$StartedUtc, [string]$Manifest,
        [Parameter(Mandatory)][string]$Path
    )
    if (-not $PSCmdlet.ShouldProcess($Path, 'Write progress')) { return }
    $tmp = "$Path.tmp"
    [ordered]@{
        Manifest    = $Manifest
        Total       = $Total
        Done        = $Done
        Remaining   = $Total - $Done
        AlreadyGone = $Gone
        Failed      = $Failed
        StartedUtc  = $StartedUtc
        UpdatedUtc  = [DateTime]::UtcNow.ToString('o')
    } | ConvertTo-Json | Set-Content -LiteralPath $tmp -Encoding utf8
    Move-Item -LiteralPath $tmp -Destination $Path -Force
}

function Resolve-StatusPath {
    # Computes the status-file path that pairs with a manifest, tagging it with
    # the script name for a clear association but avoiding a doubled tag when the
    # manifest is already named after the script. Shared by -Delete and
    # -ListDeleted so both always agree on where progress lives.
    [CmdletBinding()]
    [OutputType([string])]
    param(
        [Parameter(Mandatory)][string]$Manifest,
        [Parameter(Mandatory)][string]$Directory,
        [Parameter(Mandatory)][string]$Extension,
        [string]$ScriptTag
    )
    if (-not $ScriptTag) { $ScriptTag = 'Find-DropboxConflicts' }
    $leaf = Split-Path -Leaf $Manifest
    $baseName = if ($leaf.StartsWith("$ScriptTag.", [System.StringComparison]::OrdinalIgnoreCase)) {
        $leaf
    }
    else { "$ScriptTag.$leaf" }
    return (Join-Path $Directory "$baseName.$Extension")
}

# When dot-sourced (e.g. by Pester) load the helpers above but skip execution.
if ($MyInvocation.InvocationName -eq '.') { return }

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# --- -ListDeleted: report what has already been deleted (no API/module needed) ---
# Deletion walks the manifest strictly top-to-bottom, so the already-deleted rows
# are exactly the first <Done> data rows. Read that count from the progress JSON.
if ($ListDeleted) {
    if (-not (Test-Path -LiteralPath $OutputCsv)) {
        throw "Manifest not found: $OutputCsv"
    }
    $scriptTag = [System.IO.Path]::GetFileNameWithoutExtension($PSCommandPath)
    if (-not $ProgressPath) {
        $ProgressPath = Resolve-StatusPath -Manifest $OutputCsv -Directory $ProgressDirectory -Extension 'progress.json' -ScriptTag $scriptTag
    }
    if (-not (Test-Path -LiteralPath $ProgressPath)) {
        Write-Host "No progress file yet ($ProgressPath); nothing has been deleted." -ForegroundColor Yellow
        return
    }
    $state = Get-Content -LiteralPath $ProgressPath -Raw | ConvertFrom-Json
    $done = [int]$state.Done
    Write-Host ("{0:N0} deleted so far (of {1:N0}); {2:N0} remaining. Last update {3}." -f `
            $done, [int]$state.Total, [int]$state.Remaining, $state.UpdatedUtc) -ForegroundColor Cyan
    if ($done -le 0) { return }

    # Stream the first $done paths; with -Tail N, keep only the last N of those.
    $emit = {
        $reader = [System.IO.StreamReader]::new($OutputCsv)
        try {
            $reader.ReadLine() | Out-Null   # header
            for ($i = 0; $i -lt $done; $i++) {
                $line = $reader.ReadLine()
                if ($null -eq $line) { break }
                $p = Get-CsvPathField -Line $line
                if ($p) { $p }
            }
        }
        finally { $reader.Dispose() }
    }
    if ($Tail -gt 0) { & $emit | Select-Object -Last $Tail }
    else { & $emit }
    return
}

# --- Ensure the module is loaded and a Dropbox drive is mounted ------------
if (-not (Get-Module DbxProvider)) {
    if (-not (Test-Path -LiteralPath $ModulePath)) {
        throw "DbxProvider module not found at '$ModulePath'. Build it or pass -ModulePath."
    }
    Import-Module $ModulePath -ErrorAction Stop
}

$driveName = Resolve-DbxDriveName -Path $StartPath
if (-not (Get-PSDrive -Name $driveName -ErrorAction SilentlyContinue)) {
    # Connect-Dropbox owns the transient "Connecting..." progress and the final
    # "Connected to Dropbox as ..." line, so the script does not print its own.
    Connect-Dropbox -DriveName $driveName | Out-Null
}

# --- Cache-backed scan via the provider cmdlet -----------------------------
# Find-DropboxConflict reads the local metadata cache (zero API enumeration) and
# auto-refreshes it from the account delta cursor first. Build/refresh the cache
# with Build-DropboxCacheAll.ps1. Passing -StatePath lets the cmdlet archive any
# legacy *.state.json sidecar left next to the manifest by an older version.
if ($SkipScan) {
    Write-Host 'Skipping scan (-SkipScan); using the existing manifest.' -ForegroundColor Cyan
}
else {
    $statePath = [System.IO.Path]::ChangeExtension($OutputCsv, '.state.json')

    Write-Host 'Scanning for conflict files (cache-backed)...' -ForegroundColor Cyan
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $conflicts = @(Find-DropboxConflict -Path $StartPath -Pattern $Pattern -DriveName $driveName `
            -IncludeNonZero:$IncludeNonZero -StatePath $statePath)

    # Merge (append + de-dupe) into the existing manifest instead of overwriting it,
    # so results from a prior scan are preserved.
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

    Write-Host ("Scan complete in {0}s. matched={1} (added {2} new). Manifest: {3}" -f `
            [int]$sw.Elapsed.TotalSeconds, $conflicts.Count, $added, $OutputCsv) -ForegroundColor Green
}

# --- Optional deletion (resumable, with a session-surviving countdown) ------
if (-not $Delete) {
    Write-Host ''
    Write-Host 'To delete the captured files, preview then run:' -ForegroundColor Cyan
    Write-Host "  ./Find-DropboxConflicts.ps1 -Delete -WhatIf" -ForegroundColor Gray
    Write-Host "  ./Find-DropboxConflicts.ps1 -Delete" -ForegroundColor Gray
    return
}

if (-not (Test-Path -LiteralPath $OutputCsv)) {
    Write-Host 'Nothing to delete (no manifest).' -ForegroundColor Yellow
    return
}
if (-not (Test-Path -LiteralPath $ProgressDirectory)) {
    New-Item -ItemType Directory -Path $ProgressDirectory -Force | Out-Null
}
$scriptTag = [System.IO.Path]::GetFileNameWithoutExtension($PSCommandPath)
if (-not $ProgressPath) {
    $ProgressPath = Resolve-StatusPath -Manifest $OutputCsv -Directory $ProgressDirectory -Extension 'progress.json' -ScriptTag $scriptTag
}
$failedCsv = Resolve-StatusPath -Manifest $OutputCsv -Directory $ProgressDirectory -Extension 'failed.csv' -ScriptTag $scriptTag
if (-not (Test-Path -LiteralPath $failedCsv)) { Set-Content -LiteralPath $failedCsv -Value 'Path,Reason' -Encoding utf8 }

$total = Measure-FileLine -Path $OutputCsv
$done = 0
$gone = 0
# Real failures are durably recorded (one row each) in the failed.csv sidecar. That
# file is the rolling backlog of items that failed on EARLIER runs; it is retried
# up front (its own phase, below) before the manifest is processed. The live delete
# line therefore counts only NEW failures from THIS run ($failed starts at 0) so the
# counter is not pre-seeded with stale carryover from previous runs.
$priorFailedCount = Measure-FileLine -Path $failedCsv
$failed = 0
# Each DISTINCT failure reason is surfaced to the console at most once per run; further
# occurrences are folded into the live "N failed" counter on the status line so a
# recurring batch error cannot spam warnings that break the in-place line overwrite.
$seenFailureReasons = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
if ((-not $ResetProgress) -and (Test-Path -LiteralPath $ProgressPath)) {
    try {
        $saved = Get-Content -LiteralPath $ProgressPath -Raw | ConvertFrom-Json
        $done = [int]$saved.Done
        if ($saved.PSObject.Properties['AlreadyGone']) { $gone = [int]$saved.AlreadyGone }
        if ($done -gt $total) { $done = $total }
        Write-Host ("Resuming delete: {0:N0} of {1:N0} already processed." -f $done, $total) -ForegroundColor Cyan
    }
    catch { Write-Warning "Could not read progress '$ProgressPath'; starting over. $($_.Exception.Message)" }
}
# Concurrency is a fixed, independent lever (default 4): it controls how many
# delete_batch jobs overlap, which is what drives namespace write-lock contention
# (the transient too_many_write_operations errors). BatchSize is decoupled from it
# -- a smaller batch finishes (and so advances the progress bar) more often WITHOUT
# adding overlap, so progress stays visible during the multi-minute server-side
# wait. Each cmdlet call covers one wave (BatchSize * concurrency), so the outer
# countdown and the resumable checkpoint advance every wave.
$concurrency = $MaxConcurrency
$windowSize = $BatchSize * $concurrency

$manifestRemaining = [Math]::Max(0, $total - $done)
if ($manifestRemaining -le 0 -and $priorFailedCount -le 0) {
    Write-Host ("Nothing to delete: all {0:N0} rows already processed. (Use -ResetProgress to start over.)" -f $total) -ForegroundColor Green
    return
}
$plannedThisRun = if ($Limit -gt 0) { [Math]::Min($Limit, $manifestRemaining) } else { $manifestRemaining }
if (-not $PSCmdlet.ShouldProcess(("{0:N0} item(s) from {1}" -f ($plannedThisRun + $priorFailedCount), $OutputCsv), 'Batch delete')) {
    return   # -WhatIf prints here; the saved counter is left untouched.
}

$startedUtc = [DateTime]::UtcNow.ToString('o')
$runStart = [System.Diagnostics.Stopwatch]::StartNew()
$processedThisRun = 0

# Suppress ALL Write-Progress for the delete work below. The script renders its own
# single-line green status via carriage-return overwrite; leaving Write-Progress on
# lets the Remove-DropboxItemBatch inner bar repaint the console concurrently and
# corrupt that line. Connect-Dropbox (above) already finished, so its own progress is
# unaffected. (The cmdlet keeps its bar for callers that invoke it directly.)
$ProgressPreference = 'SilentlyContinue'

# In-place rendering is only safe when nothing else writes between status updates.
# Fall back to one line per update when output is redirected (transcript/log/CI) or
# under -Verbose (the cmdlet streams verbose lines during each call).
$inplaceOk = (-not [Console]::IsOutputRedirected) -and ($VerbosePreference -eq 'SilentlyContinue')
$script:InplaceOk = $inplaceOk

# A background 1-second ticker keeps the status line alive WHILE a delete wave is in
# flight. Each Remove-DropboxItemBatch call blocks for a whole wave (BatchSize x
# MaxConcurrency paths, often a minute or more under throttling), so without this the
# counter would appear frozen between waves. A PowerShell Write-Progress / timer
# scriptblock cannot run on the threadpool thread (no Runspace there), so the ticker
# is a tiny C# class that owns all in-place console writes: it truncates to the
# console width (so the line never wraps and the carriage-return overwrite works),
# locks so the timer thread and the main thread never interleave, and renders a
# spinner + wave-elapsed clock between waves and the exact totals at each boundary.
$dbxConsoleStatusSource = @'
using System;
using System.Diagnostics;
using System.Threading;

public sealed class DbxConsoleStatus : IDisposable
{
    private readonly object _gate = new object();
    private Timer _timer;
    private Stopwatch _wave;
    private int _spin;
    private int _lastLen;
    private bool _waveActive;
    private long _done, _total;
    private double _rate;
    private string _eta = "?";
    private string _suffix = "";
    private double _runBaseSeconds;

    public void Start()
    {
        _timer = new Timer(_ => Tick(), null, 1000, 1000);
    }

    private static int Width()
    {
        try { int w = Console.WindowWidth; if (w > 1) return w; } catch { }
        return 120;
    }

    // Core in-place write. Caller must hold _gate.
    private void WriteCore(string text, ConsoleColor color)
    {
        int max = Width() - 1;
        if (text.Length > max) text = text.Substring(0, max);
        int pad = text.Length < _lastLen ? _lastLen - text.Length : 0;
        ConsoleColor prev = Console.ForegroundColor;
        try { Console.ForegroundColor = color; Console.Write("\r" + text + new string(' ', pad)); }
        finally { Console.ForegroundColor = prev; }
        _lastLen = text.Length;
    }

    public void Write(string text, ConsoleColor color)
    {
        lock (_gate) { WriteCore(text, color); }
    }

    public void EndLine()
    {
        lock (_gate) { if (_lastLen > 0) { Console.Write("\n"); _lastLen = 0; } }
    }

    public void BeginWave(long done, long total, double rate, string eta, string suffix, double runElapsedSeconds)
    {
        lock (_gate)
        {
            _done = done; _total = total; _rate = rate;
            _eta = eta ?? "?"; _suffix = suffix ?? "";
            _runBaseSeconds = runElapsedSeconds;
            _wave = Stopwatch.StartNew(); _waveActive = true;
        }
    }

    public void EndWave()
    {
        lock (_gate) { _waveActive = false; }
    }

    // Mirrors the PowerShell Format-Duration helper: "Xh YYm" / "Ym ZZs" / "Zs".
    private static string FormatDuration(TimeSpan span)
    {
        if (span.TotalHours >= 1) return string.Format("{0}h {1:00}m", (int)span.TotalHours, span.Minutes);
        if (span.TotalMinutes >= 1) return string.Format("{0}m {1:00}s", span.Minutes, span.Seconds);
        return string.Format("{0}s", span.Seconds);
    }

    private void Tick()
    {
        lock (_gate)
        {
            if (!_waveActive || _wave == null) return;
            char spin = "|/-\\"[_spin++ % 4];
            string elapsed = FormatDuration(TimeSpan.FromSeconds(_runBaseSeconds) + _wave.Elapsed);
            string line = string.Format(
                "[elapsed {0}] {1} deleting conflicts  {2:N0}/{3:N0} deleted   ETA {5} ({4:N0}/min){6}  Press Ctrl+C to cancel.",
                elapsed, spin, _done, _total, _rate, _eta, _suffix);
            WriteCore(line, ConsoleColor.Green);
        }
    }

    public void Dispose()
    {
        if (_timer != null) _timer.Dispose();
        lock (_gate) { _waveActive = false; }
    }
}
'@

$script:Ticker = $null
if ($inplaceOk) {
    try {
        if (-not ('DbxConsoleStatus' -as [type])) {
            Add-Type -TypeDefinition $dbxConsoleStatusSource -ErrorAction Stop
        }
        $script:Ticker = [DbxConsoleStatus]::new()
        $script:Ticker.Start()
    }
    catch {
        Write-Verbose "Live status ticker unavailable ($($_.Exception.Message)); using per-wave status only."
        $script:Ticker = $null
    }
}

# --- Rolling retry queue: re-attempt entries that failed on a previous run ---
# The manifest high-water mark ($done) has already advanced past earlier failures,
# so failed.csv is the only record of them. Re-feed it first so transient problems
# (e.g. lock contention that has since cleared) self-heal with no special action.
# Entries that still fail are written back; cleared ones drop out of the file.
$priorFailures = @(Import-Csv -LiteralPath $failedCsv -ErrorAction SilentlyContinue |
        Where-Object { $_.Path } | Select-Object -ExpandProperty Path -Unique)
$priorCleared = 0
$priorStillFailed = 0
if ($priorFailures.Count -gt 0) {
    $rfTotal = $priorFailures.Count
    Write-Host ("Retrying {0:N0} previously-failed item(s) from earlier runs..." -f $rfTotal) -ForegroundColor Cyan
    $stillFailed = [System.Collections.Generic.List[psobject]]::new()
    $rfDone = 0
    for ($off = 0; $off -lt $rfTotal; $off += $windowSize) {
        $end = [Math]::Min($off + $windowSize, $rfTotal) - 1
        $slice = @($priorFailures[$off..$end])
        $rfEv = $null
        $slice | Remove-DropboxItemBatch -DriveName $driveName -BatchSize $BatchSize -MaxConcurrency $concurrency -Confirm:$false -ErrorAction SilentlyContinue -ErrorVariable +rfEv
        if ($rfEv) {
            foreach ($e in $rfEv) {
                $parsed = Split-DeleteError -ErrorRecord $e
                if (-not $parsed.IsGone) { $stillFailed.Add($parsed) }
            }
        }
        $rfDone += $slice.Count
        $rfLine = ("  retrying previously-failed items: {0:N0} of {1:N0} done, {2:N0} remaining   Press Ctrl+C to cancel." -f `
                $rfDone, $rfTotal, ($rfTotal - $rfDone))
        Show-Status -Text $rfLine -Color Cyan
    }
    Stop-StatusLine
    # Rewrite failed.csv with only the entries that are still failing.
    Set-Content -LiteralPath $failedCsv -Value 'Path,Reason' -Encoding utf8
    foreach ($p in $stillFailed) {
        ('"{0}","{1}"' -f ($p.Path -replace '"', '""'), ($p.Reason -replace '"', '""')) |
            Add-Content -LiteralPath $failedCsv -Encoding utf8
    }
    $priorStillFailed = $stillFailed.Count
    $priorCleared = $rfTotal - $priorStillFailed
    Write-Host ("Retry pass: {0:N0} cleared, {1:N0} still failing." -f $priorCleared, $priorStillFailed) -ForegroundColor Green
}

$reader = [System.IO.StreamReader]::new($OutputCsv)
try {
    $reader.ReadLine() | Out-Null                        # header
    for ($i = 0; $i -lt $done; $i++) { $reader.ReadLine() | Out-Null }   # skip processed

    $batch = [System.Collections.Generic.List[string]]::new()
    # Status is rendered in place (carriage return, no wrap) via Show-Status when
    # $inplaceOk; a background ticker (if available) keeps it alive during each wave.
    # When output is redirected or -Verbose is on, Show-Status falls back to one line
    # per wave so logs stay clean.
    $more = $true
    while ($more) {
        $batch.Clear()
        # Cap this window so a -Limit run never deletes more than requested.
        $target = if ($Limit -gt 0) { [Math]::Min($windowSize, $Limit - $processedThisRun) } else { $windowSize }
        while ($batch.Count -lt $target) {
            $line = $reader.ReadLine()
            if ($null -eq $line) { $more = $false; break }
            if ([string]::IsNullOrWhiteSpace($line)) { continue }
            $p = Get-CsvPathField -Line $line
            if ($p) { $batch.Add($p) }
        }
        if ($batch.Count -eq 0) { break }

        if ($ShowItems) {
            Stop-StatusLine
            $batch | ForEach-Object { Write-Host "  del $_" -ForegroundColor DarkGray }
        }

        # Snapshot cumulative pace so the in-wave ticker shows a live spinner + elapsed
        # clock alongside the running totals while this blocking wave is in flight.
        $rate = if ($runStart.Elapsed.TotalMinutes -gt 0) { $processedThisRun / $runStart.Elapsed.TotalMinutes } else { 0 }
        $eta = if ($rate -gt 0) { Format-Duration ([TimeSpan]::FromMinutes(($total - $done) / $rate)) } else { '?' }
        $suffix = if ($failed) { "  {0:N0} failed" -f $failed } else { '' }
        if ($script:Ticker) { $script:Ticker.BeginWave($done, $total, $rate, $eta, $suffix, $runStart.Elapsed.TotalSeconds) }

        $ev = $null
        # The whole window is deleted by ONE blocking call. The cmdlet's own progress
        # bar is suppressed ($ProgressPreference='SilentlyContinue' above); the ticker
        # owns the single in-place line so nothing collides with it.
        $batch | Remove-DropboxItemBatch -DriveName $driveName -BatchSize $BatchSize -MaxConcurrency $concurrency -Confirm:$false -ErrorAction SilentlyContinue -ErrorVariable +ev
        if ($script:Ticker) { $script:Ticker.EndWave() }
        if ($ev) {
            foreach ($e in $ev) {
                $parsed = Split-DeleteError -ErrorRecord $e
                if ($parsed.IsGone) {
                    $gone++   # already deleted (e.g. parent conflict folder removed earlier) -- benign
                }
                else {
                    $failed++
                    # Record every real failure (with its real path) so it is retried on
                    # the next run. Only the FIRST occurrence of each distinct reason is
                    # printed -- repeats just advance the "N failed" status counter -- so a
                    # recurring error does not break the in-place line with a warning flood.
                    ('"{0}","{1}"' -f ($parsed.Path -replace '"', '""'), ($parsed.Reason -replace '"', '""')) |
                        Add-Content -LiteralPath $failedCsv -Encoding utf8
                    if ($seenFailureReasons.Add([string]$parsed.Reason)) {
                        Stop-StatusLine
                        Write-Warning ("delete failed ({0}); further occurrences are counted on the status line." -f $parsed.Reason)
                    }
                }
            }
        }
        $done += $batch.Count
        $processedThisRun += $batch.Count
        Save-Progress -Done $done -Total $total -Failed $failed -Gone $gone -StartedUtc $startedUtc -Manifest $OutputCsv -Path $ProgressPath

        $remaining = $total - $done
        $rate = if ($runStart.Elapsed.TotalMinutes -gt 0) { $processedThisRun / $runStart.Elapsed.TotalMinutes } else { 0 }
        $eta = if ($rate -gt 0) { Format-Duration ([TimeSpan]::FromMinutes($remaining / $rate)) } else { '?' }
        # New failures THIS run are shown inline; already-gone (parent-folder cascade)
        # is folded into the deleted count and reported only in the end summary.
        $suffix = if ($failed) { "   {0:N0} failed" -f $failed } else { '' }
        $statusLine = ("[elapsed {0}] deleted {1,12:N0} / {2:N0} ({3:N0} remaining)   ETA {4} ({5:N0}/min){6}   Press Ctrl+C to cancel." -f `
                (Format-Duration $runStart.Elapsed), $done, $total, $remaining, $eta, $rate, $suffix)
        Show-Status -Text $statusLine -Color Green

        if ($Limit -gt 0 -and $processedThisRun -ge $Limit) { break }   # -Limit reached
    }
}
finally {
    $reader.Dispose()
    if ($script:Ticker) {
        $script:Ticker.EndWave()
        $script:Ticker.EndLine()
        $script:Ticker.Dispose()
        $script:Ticker = $null
    }
}

# Terminate the in-place status line so the summary below is not written over it.
Stop-StatusLine

# --- End summary -----------------------------------------------------------
# Break the outcome into its distinct buckets instead of one ambiguous number:
#   * deleted     -- rows processed from the manifest this run ($done is cumulative);
#   * already gone -- removed by an earlier parent-folder delete that cascaded
#                     (counted as resolved, never a failure);
#   * new failures -- genuine failures THIS run, appended to failed.csv;
#   * prior retry  -- how the up-front retry of earlier failures resolved.
$parts = [System.Collections.Generic.List[string]]::new()
if ($gone) { $parts.Add(("{0:N0} already removed by earlier parent-folder deletes" -f $gone)) }
if ($failed) { $parts.Add(("{0:N0} new failure(s) this run -- see {1}" -f $failed, $failedCsv)) }
if ($priorFailures.Count -gt 0) {
    $parts.Add(("prior failures: {0:N0} cleared, {1:N0} still failing" -f $priorCleared, $priorStillFailed))
}
$summary = if ($parts.Count -gt 0) { ' ' + ($parts -join '; ') + '.' } else { '' }
if ($done -ge $total) {
    Write-Host ("Done. Processed all {0:N0} rows in {1}.{2}" -f `
            $total, (Format-Duration $runStart.Elapsed), $summary) -ForegroundColor Green
}
else {
    $why = if ($Limit -gt 0 -and $processedThisRun -ge $Limit) { " (-Limit $Limit reached)" } else { '' }
    Write-Host ("Stopped at {0:N0} of {1:N0}{2}.{3} Re-run with -Delete to continue (increase or drop -Limit to go further)." -f $done, $total, $why, $summary) -ForegroundColor Yellow
}
if ($failed -or $priorStillFailed) {
    $outstanding = $failed + $priorStillFailed
    Write-Host ("{0:N0} item(s) still failing after in-batch retries; recorded in {1} and retried automatically at the start of the next -Delete run (no special action needed)." -f $outstanding, $failedCsv) -ForegroundColor Gray
}