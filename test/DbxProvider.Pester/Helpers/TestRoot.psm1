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

    # Note: each test gets its own GUID-suffixed subfolder via New-DbxTestFolder,
    # so a wholesale purge here is best-effort only. Don't fail the test file's
    # BeforeAll if Dropbox returns rate-limit / conflict / scope errors.
    if (Test-Path -LiteralPath $providerRoot) {
        try {
            Remove-Item -LiteralPath $providerRoot -Recurse -Force -ErrorAction Stop
        }
        catch {
            try {
                Remove-Item -LiteralPath $providerRoot -Recurse -ErrorAction Stop
            }
            catch {
                Write-Warning "Failed to purge existing test root '$providerRoot' (continuing): $_"
            }
        }
    }

    # Rate-limit / soft-throttle retries are handled by the provider itself
    # (see RateLimitRetry.cs). path/conflict/folder just means another
    # concurrent test file already created the root; treat it as success.
    try {
        if (-not (Test-Path -LiteralPath $providerRoot)) {
            New-Item -Path $providerRoot -ItemType Directory -Force -ErrorAction Stop | Out-Null
        }
    }
    catch {
        if ($_.Exception.Message -notmatch 'path/conflict/folder|already_exists') { throw }
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

    # Rate-limit / soft-throttle retries are handled by the provider; only
    # benign create-conflicts (another test created the same path) need
    # special handling here.
    try {
        New-Item -Path $providerPath -ItemType Directory -Force -ErrorAction Stop | Out-Null
    }
    catch {
        if ($_.Exception.Message -notmatch 'path/conflict/folder|already_exists') { throw }
    }

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
