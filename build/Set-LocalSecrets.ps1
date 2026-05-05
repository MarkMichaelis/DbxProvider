#requires -Version 7.4
<#
.SYNOPSIS
    Interactively seed dotnet user-secrets for the DbxProvider functional tests.

.DESCRIPTION
    Prompts for AppKey, AppSecret, RefreshToken (and optional TestMemberEmail)
    and stores them via 'dotnet user-secrets' against the functional test
    project. Existing values are surfaced as defaults where safe.
#>
[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$csproj   = Join-Path $repoRoot 'test\DbxProvider.FunctionalTests\DbxProvider.FunctionalTests.csproj'

if (-not (Test-Path $csproj)) {
    throw "Functional test project not found at $csproj. Has it been created yet?"
}

function Read-SecureValue {
    param([Parameter(Mandatory)] [string]$Prompt)
    $secure = Read-Host -Prompt $Prompt -AsSecureString
    if ($null -eq $secure -or $secure.Length -eq 0) { return $null }
    $bstr = [System.Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure)
    try   { [System.Runtime.InteropServices.Marshal]::PtrToStringBSTR($bstr) }
    finally { [System.Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr) }
}

function Get-ExistingSecret {
    param([Parameter(Mandatory)] [string]$Name)
    try {
        $list = & dotnet user-secrets list --project $csproj 2>$null
        if ($LASTEXITCODE -ne 0) { return $null }
        foreach ($line in $list) {
            if ($line -match "^\s*$([regex]::Escape($Name))\s*=\s*(.*)$") {
                return $Matches[1]
            }
        }
    } catch { }
    return $null
}

function Set-Secret {
    param(
        [Parameter(Mandatory)] [string]$Name,
        [Parameter(Mandatory)] [AllowEmptyString()] [string]$Value
    )
    if ([string]::IsNullOrEmpty($Value)) {
        Write-Host "  - $Name : (unchanged / not set)" -ForegroundColor DarkGray
        return
    }
    & dotnet user-secrets set $Name $Value --project $csproj | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw "Failed to set user-secret '$Name'."
    }
    Write-Host "  - $Name : set" -ForegroundColor Green
}

Write-Host "Initializing user-secrets for $csproj" -ForegroundColor Cyan
& dotnet user-secrets init --project $csproj | Out-Null
if ($LASTEXITCODE -ne 0) {
    throw 'dotnet user-secrets init failed.'
}

$existingAppKey = Get-ExistingSecret -Name 'DBX_APP_KEY'
$appKeyPrompt   = if ($existingAppKey) {
    "AppKey [$existingAppKey]"
} else {
    'AppKey'
}
$appKeyInput = Read-Host -Prompt $appKeyPrompt
if ([string]::IsNullOrWhiteSpace($appKeyInput)) { $appKeyInput = $existingAppKey }

$appSecret    = Read-SecureValue -Prompt 'AppSecret (input hidden; blank = leave unchanged)'
$refreshToken = Read-SecureValue -Prompt 'RefreshToken (input hidden; blank = leave unchanged)'
$memberEmail  = Read-Host -Prompt 'TestMemberEmail (optional, blank = leave unchanged)'

Write-Host ""
Write-Host 'Storing values...' -ForegroundColor Cyan
Set-Secret -Name 'DBX_APP_KEY'           -Value ($appKeyInput  ?? '')
Set-Secret -Name 'DBX_APP_SECRET'        -Value ($appSecret    ?? '')
Set-Secret -Name 'DBX_REFRESH_TOKEN'     -Value ($refreshToken ?? '')
Set-Secret -Name 'DBX_TEST_MEMBER_EMAIL' -Value ($memberEmail  ?? '')

Write-Host ""
Write-Host 'Current user-secrets:' -ForegroundColor Cyan
& dotnet user-secrets list --project $csproj
