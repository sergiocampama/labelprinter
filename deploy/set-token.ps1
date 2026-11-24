<#
    Stores a GitHub token for publishing releases, encrypted with Windows DPAPI
    in %USERPROFILE%\.labelprinter\github-token.dat. Only this Windows user on
    this PC can decrypt it.

    The token needs 'Contents: Read and write' on sergiocampama/labelprinter
    (fine-grained PAT), or the classic 'repo' scope.

    Usage (in your own PowerShell window):
      ./deploy/set-token.ps1
#>
[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"

$dir  = Join-Path $env:USERPROFILE ".labelprinter"
$path = Join-Path $dir "github-token.dat"
New-Item -ItemType Directory -Force -Path $dir | Out-Null

$secure = Read-Host -Prompt "Paste GitHub token (input hidden)" -AsSecureString
if ($secure.Length -eq 0) { throw "No token entered." }

$secure | ConvertFrom-SecureString | Set-Content -Path $path -Encoding ascii
Write-Host "Token stored (DPAPI-encrypted) at: $path" -ForegroundColor Green
