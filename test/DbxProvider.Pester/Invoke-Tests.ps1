[CmdletBinding()]
param(
    [string]$OutputPath
)

# StrictMode intentionally NOT set here. Pester v5's It-block scoping interacts
# poorly with 'Set-StrictMode -Version Latest' (bare $script-scoped variables
# referenced in It blocks raise 'cannot be retrieved' errors), and tests then
# silently fall through to the FileSystem provider when paths like
# "$($Folder.ProviderPath)\..." evaluate to empty.
$ErrorActionPreference = 'Stop'

$root = $PSScriptRoot
$repoRoot = (Resolve-Path (Join-Path $root '..\..')).Path

if (-not $OutputPath) {
    $OutputPath = Join-Path $repoRoot 'TestResults\Pester.xml'
}
$outDir = Split-Path -Parent $OutputPath
if (-not (Test-Path $outDir)) {
    New-Item -ItemType Directory -Path $outDir -Force | Out-Null
}

Import-Module Pester -MinimumVersion 5.5.0 -Force
Import-Module (Join-Path $root 'Helpers\TestEnvironment.psm1') -Force
Import-Module (Join-Path $root 'Helpers\TestRoot.psm1') -Force

$secrets = Get-DbxTestSecrets
if ($secrets.RefreshToken -and $secrets.AppKey) {
    try {
        Import-DbxProviderModule
        Connect-DbxTestDrive
        Initialize-DbxTestRoot
        Disconnect-DbxTestDrive
    }
    catch {
        Write-Warning "Pre-test initialization failed: $_"
    }
}
else {
    Write-Warning "DBX_APP_KEY / DBX_REFRESH_TOKEN not set; integration tests will be skipped."
}

$config = New-PesterConfiguration
$config.Run.Path = (Join-Path $root 'Tests')
$config.Run.Exit = $false
$config.Run.PassThru = $true
$config.Output.Verbosity = 'Detailed'
$config.TestResult.Enabled = $true
$config.TestResult.OutputFormat = 'NUnitXml'
$config.TestResult.OutputPath = $OutputPath

$result = Invoke-Pester -Configuration $config

if ($result.FailedCount -gt 0) {
    exit 1
}
exit 0
