Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$script:TestRoot = '/DbxProviderTests'
$script:Initialized = $false

function Get-DbxTestRoot {
    [CmdletBinding()]
    param()
    return $script:TestRoot
}

function Initialize-DbxTestRoot {
    [CmdletBinding()]
    param(
        [string]$DriveName = 'DbxTest'
    )

    if ($script:Initialized) { return }

    $providerRoot = "${DriveName}:\DbxProviderTests"
    if (Test-Path -LiteralPath $providerRoot) {
        try {
            # Try a hard delete first (permanent_delete). If the app's token
            # lacks files.permanent_delete scope, fall back to soft delete so
            # the test run can proceed.
            Remove-Item -LiteralPath $providerRoot -Recurse -Force -ErrorAction Stop
        }
        catch {
            try {
                Remove-Item -LiteralPath $providerRoot -Recurse -ErrorAction Stop
            }
            catch {
                Write-Warning "Failed to purge existing test root '$providerRoot': $_"
            }
        }
    }

    if (-not (Test-Path -LiteralPath $providerRoot)) {
        New-Item -Path $providerRoot -ItemType Directory -Force | Out-Null
    }

    $script:Initialized = $true
}

function Reset-DbxTestRoot {
    [CmdletBinding()]
    param()
    $script:Initialized = $false
}

function New-DbxTestFolder {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$TestName,

        [string]$DriveName = 'DbxTest'
    )

    $safe = ($TestName -replace '[^A-Za-z0-9_\-]', '_')
    $guid = [Guid]::NewGuid().ToString('N').Substring(0, 8)
    $apiPath = "$script:TestRoot/$safe-$guid"
    $providerPath = "${DriveName}:\DbxProviderTests\$safe-$guid"

    New-Item -Path $providerPath -ItemType Directory -Force | Out-Null

    return [pscustomobject]@{
        ApiPath      = $apiPath
        ProviderPath = $providerPath
        Name         = "$safe-$guid"
    }
}

function Remove-DbxTestFolder {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory, ValueFromPipeline = $true, ValueFromPipelineByPropertyName = $true)]
        [Alias('ProviderPath')]
        [string]$Path
    )

    process {
        if (Test-Path -LiteralPath $Path) {
            try {
                Remove-Item -LiteralPath $Path -Recurse -Force -ErrorAction Stop
            }
            catch {
                Write-Verbose "Cleanup of '$Path' failed: $_"
            }
        }
    }
}

Export-ModuleMember -Function Get-DbxTestRoot, Initialize-DbxTestRoot, Reset-DbxTestRoot, New-DbxTestFolder, Remove-DbxTestFolder
