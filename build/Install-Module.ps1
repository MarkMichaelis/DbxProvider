# Install-Module.ps1
#
# Copies the built DbxProvider module into a real PowerShell module path so
# you can `Import-Module DbxProvider` from any session WITHOUT locking the
# build output directory. This lets you keep iterating in
# src\DbxProvider\bin\<Config>\net8.0 while a separate pwsh session has the
# module loaded from the install location.
#
# Default install location (CurrentUser scope, PowerShell 7+):
#   $HOME\Documents\PowerShell\Modules\DbxProvider\<version>
#
# Usage:
#   pwsh -NoProfile -File .\build\Install-Module.ps1                       # CurrentUser, Debug
#   pwsh -NoProfile -File .\build\Install-Module.ps1 -Configuration Release
#   pwsh -NoProfile -File .\build\Install-Module.ps1 -Scope AllUsers       # requires admin
#   pwsh -NoProfile -File .\build\Install-Module.ps1 -Destination C:\tmp\Mods
#
# After installing, in any NEW pwsh session:
#   Import-Module DbxProvider
#   Get-Module DbxProvider     # confirms it loaded from the install path
#
# Side-by-side dev install (lets you keep a stable copy installed AND a
# work-in-progress copy installed at the same time, each loadable by name):
#   pwsh -NoProfile -File .\build\Install-Module.ps1 -Name DbxProvider.Dev
#
#   # Session A (stable):
#   pwsh
#   Import-Module DbxProvider
#
#   # Session B (work-in-progress):
#   pwsh
#   Import-Module DbxProvider.Dev
#
# NOTE: Both installs CANNOT be loaded into the same pwsh process — they
# share an assembly identity (DbxProvider.dll, same version), and .NET
# refuses to load two assemblies with the same strong name into one ALC.
# Use separate pwsh sessions instead, or `pwsh -NoProfile -Command "...";`
# subprocesses for short-lived comparisons.
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug',

    [ValidateSet('CurrentUser', 'AllUsers')]
    [string]$Scope = 'CurrentUser',

    # Module name to install as. Defaults to 'DbxProvider'. Set to something
    # else (e.g. 'DbxProvider.Dev') to install side-by-side with another
    # build under a different identity.
    [ValidatePattern('^[A-Za-z][A-Za-z0-9_.-]*$')]
    [string]$Name = 'DbxProvider',

    # Override the destination root (a "<Name>\<version>" subfolder is
    # created underneath). When omitted, derived from -Scope.
    [string]$Destination,

    # Skip the dotnet build step (assumes the configuration is already built).
    [switch]$NoBuild,

    # Force-remove the existing install even if files appear locked. Will fail
    # with a clear message if processes still hold the DLLs.
    [switch]$Force
)

$ErrorActionPreference = 'Stop'

$repoRoot   = Split-Path -Parent $PSScriptRoot
$projPath   = Join-Path $repoRoot 'src\DbxProvider\DbxProvider.csproj'
$outDir     = Join-Path $repoRoot "src\DbxProvider\bin\$Configuration\net8.0"
$manifest   = Join-Path $outDir 'DbxProvider.psd1'

if (-not $NoBuild) {
    Write-Host "Building DbxProvider ($Configuration)..." -ForegroundColor Cyan
    & dotnet build $projPath -c $Configuration --nologo -v minimal
    if ($LASTEXITCODE -ne 0) { throw "dotnet build failed (exit $LASTEXITCODE)." }
}

if (-not (Test-Path $manifest)) {
    throw "Module manifest not found at '$manifest'. Build first or check -Configuration."
}

$psd1    = Import-PowerShellDataFile -Path $manifest
$version = $psd1.ModuleVersion
if (-not $version) { throw "ModuleVersion missing from $manifest." }

if (-not $Destination) {
    if ($Scope -eq 'AllUsers') {
        $Destination = if ($IsWindows -or $env:OS -eq 'Windows_NT') {
            Join-Path $env:ProgramFiles 'PowerShell\Modules'
        } else {
            '/usr/local/share/powershell/Modules'
        }
    } else {
        $docs = if ($IsWindows -or $env:OS -eq 'Windows_NT') {
            [Environment]::GetFolderPath('MyDocuments')
        } else {
            "$HOME/.local/share"
        }
        $Destination = Join-Path $docs 'PowerShell\Modules'
    }
}

$targetRoot   = Join-Path $Destination $Name
$targetPath   = Join-Path $targetRoot $version

Write-Host "Installing '$Name' $version to: $targetPath" -ForegroundColor Cyan

if (Test-Path $targetPath) {
    Write-Host "Removing existing install..." -ForegroundColor DarkGray
    try {
        Remove-Item $targetPath -Recurse -Force -ErrorAction Stop
    }
    catch {
        if (-not $Force) {
            throw @"
Failed to remove '$targetPath': $_
The DLL is likely loaded by a running pwsh session.
Close any pwsh sessions that have imported $Name, then re-run this script
(or pass -Force to retry after killing offending processes yourself).
"@
        }

        $dll = Join-Path $targetPath 'DbxProvider.dll'
        $holders = @()
        foreach ($p in Get-Process) {
            try {
                foreach ($m in $p.Modules) {
                    if ($m.FileName -ieq $dll) {
                        if ($p.Id -ne $PID) { $holders += $p }
                        break
                    }
                }
            } catch { }
        }
        if ($holders) {
            $list = ($holders | ForEach-Object { "  PID $($_.Id) $($_.ProcessName)" }) -join "`n"
            throw "DbxProvider.dll is locked by:`n$list`nClose those processes, then retry."
        }
        Remove-Item $targetPath -Recurse -Force
    }
}

New-Item -ItemType Directory -Path $targetPath -Force | Out-Null

# Copy the entire build output (assemblies, manifest, format file, runtimes
# folder, help xml, deps.json, etc.). Excludes pdb/xml docs to keep the install
# trim — drop the -Exclude if you want full PDB symbols too.
Copy-Item -Path (Join-Path $outDir '*') `
          -Destination $targetPath `
          -Recurse -Force `
          -Exclude '*.pdb'

# When installing under an alternate name (e.g. DbxProvider.Dev), PowerShell's
# module discovery requires the manifest filename to match the folder name.
# Rename DbxProvider.psd1 -> <Name>.psd1 so `Import-Module <Name>` picks it up.
# The manifest's RootModule still points at DbxProvider.dll (correct — it's a
# relative path inside this folder), and the module's reported name is taken
# from the manifest filename, giving each install a distinct identity.
if ($Name -ne 'DbxProvider') {
    $installedManifest = Join-Path $targetPath 'DbxProvider.psd1'
    $renamedManifest   = Join-Path $targetPath ($Name + '.psd1')
    if (Test-Path $installedManifest) {
        Move-Item -Path $installedManifest -Destination $renamedManifest -Force
    }
}

Write-Host ""
Write-Host "Installed $Name $version to:" -ForegroundColor Green
Write-Host "  $targetPath"
Write-Host ""
Write-Host "Use it from a NEW pwsh session:" -ForegroundColor Yellow
Write-Host "  Import-Module $Name"
Write-Host "  (Get-Module $Name).Path"
Write-Host ""
Write-Host "Continue building in src\DbxProvider\bin\$Configuration\net8.0;"
Write-Host "rerun this script to refresh the installed copy."
