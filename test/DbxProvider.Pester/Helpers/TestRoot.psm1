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

    # Create the root with retries that tolerate transient rate limiting
    # (too_many_write_operations) and conflicts (path/conflict/folder, which
    # just means another concurrent test file already created it).
    $attempt = 0
    while ($true) {
        try {
            if (Test-Path -LiteralPath $providerRoot) { break }
            New-Item -Path $providerRoot -ItemType Directory -Force -ErrorAction Stop | Out-Null
            break
        }
        catch {
            $msg = $_.Exception.Message
            if ($msg -match 'path/conflict/folder|already_exists') {
                # Folder is already there; that's fine.
                break
            }
            if ($msg -match 'too_many_write_operations|too_many_requests|rate_limit' -and $attempt -lt 6) {
                $attempt++
                $delay = [Math]::Min(30, [Math]::Pow(2, $attempt))
                Write-Warning "Rate-limited creating test root; sleeping ${delay}s (attempt $attempt/6)."
                Start-Sleep -Seconds $delay
                continue
            }
            throw
        }
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

    # Retry on transient Dropbox rate limiting and benign create-conflicts.
    $attempt = 0
    while ($true) {
        try {
            New-Item -Path $providerPath -ItemType Directory -Force -ErrorAction Stop | Out-Null
            break
        }
        catch {
            $msg = $_.Exception.Message
            if ($msg -match 'path/conflict/folder|already_exists') { break }
            if ($msg -match 'too_many_write_operations|too_many_requests|rate_limit' -and $attempt -lt 6) {
                $attempt++
                $delay = [Math]::Min(30, [Math]::Pow(2, $attempt))
                Write-Warning "Rate-limited creating test folder; sleeping ${delay}s (attempt $attempt/6)."
                Start-Sleep -Seconds $delay
                continue
            }
            throw
        }
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
