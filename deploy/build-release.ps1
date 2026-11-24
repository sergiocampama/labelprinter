<#
    Builds (and optionally publishes) a LabelPrinter release with Velopack.

    Stages:
      1. dotnet publish  -> self-contained win-x64 (x64, and Windows 11 on ARM via emulation)
      2. vpk pack        -> deploy/Releases (Setup.exe, *.nupkg, releases.win.json, delta)
      3. vpk upload      -> GitHub releases of this same public repo (only with -Publish),
                            plus install.ps1 and labelprinter-codesign.cer as extra assets

    Run-at-startup is handled by Velopack: the "Startup" shortcut is created in
    the user's Startup folder at install time and kept across updates.

    Requirements:
      dotnet tool install --global vpk --version 1.2.0
      The signing cert in Cert:\CurrentUser\My (see deploy/signing/decrypt-pfx.ps1).

    Examples:
      ./deploy/build-release.ps1 -Version 1.0.0            # build + pack locally
      ./deploy/build-release.ps1 -Version 1.0.1 -Publish   # also upload (token from deploy/set-token.ps1)
#>
[CmdletBinding()]
param(
    [string]$Version = "1.0.0",
    [string]$Configuration = "Release",

    # Code-signing cert (CN=LabelPrinter) in Cert:\CurrentUser\My. Signs the
    # app and installer. Machines trust it via install.ps1.
    [string]$SignThumbprint = "9F9528D62D01C87ED4EABD1FCEBF2232C5A13F6A",

    # Build without signing (local experiments only; never publish unsigned).
    [switch]$AllowUnsigned,

    [switch]$Publish,

    # Public repo that holds both the source and the releases.
    [string]$RepoUrl = "https://github.com/sergiocampama/labelprinter",

    # GitHub token with Contents: read/write on the repo. Resolution order:
    #   1. -Token  2. $env:GITHUB_TOKEN  3. DPAPI file from deploy/set-token.ps1
    [string]$Token = $env:GITHUB_TOKEN
)

$ErrorActionPreference = "Stop"

function Resolve-GitHubToken([string]$explicit) {
    if (-not [string]::IsNullOrWhiteSpace($explicit)) { return $explicit }
    $tokenFile = Join-Path $env:USERPROFILE ".labelprinter\github-token.dat"
    if (Test-Path $tokenFile) {
        try {
            $secure = Get-Content $tokenFile | ConvertTo-SecureString
            $bstr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure)
            try { return [Runtime.InteropServices.Marshal]::PtrToStringAuto($bstr) }
            finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr) }
        }
        catch {
            throw "Failed to decrypt $tokenFile (created by another Windows user?). Re-run deploy/set-token.ps1."
        }
    }
    return $null
}

$repoRoot    = Split-Path -Parent $PSScriptRoot
$projPath    = Join-Path $repoRoot "LabelPrinter.csproj"
$publishDir  = Join-Path $repoRoot "bin\publish-x64"
$releasesDir = Join-Path $PSScriptRoot "Releases"
$iconPath    = Join-Path $repoRoot "Assets\labelprinter.ico"

$signed = $SignThumbprint -and (Test-Path "Cert:\CurrentUser\My\$SignThumbprint")
if (-not $signed) {
    if ($Publish -or -not $AllowUnsigned) {
        throw "Signing cert $SignThumbprint is not in Cert:\CurrentUser\My. Restore it with deploy/signing/decrypt-pfx.ps1 (or pass -AllowUnsigned for a local-only build)."
    }
    Write-Host "    (building UNSIGNED - local use only)" -ForegroundColor Yellow
}

$token = Resolve-GitHubToken $Token
if ($Publish -and [string]::IsNullOrWhiteSpace($token)) {
    throw "Publishing needs a token: run deploy/set-token.ps1, set `$env:GITHUB_TOKEN, or pass -Token."
}

# Make the dotnet global tools dir (vpk) reachable.
$toolsDir = Join-Path $env:USERPROFILE ".dotnet\tools"
if ($env:PATH -notlike "*$toolsDir*") { $env:PATH = "$toolsDir;$env:PATH" }

Write-Host "==> [1/3] Publishing self-contained win-x64 ($Configuration) v$Version..." -ForegroundColor Cyan
if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }
dotnet publish $projPath -c $Configuration -r win-x64 --self-contained true -p:Version=$Version -o $publishDir
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed." }

# Fetch the latest published release so vpk can build a delta against it.
# The repo is public, so no token is needed. None yet on the first release.
if ($Publish) {
    Write-Host "==> Syncing the latest release from $RepoUrl (for deltas)..." -ForegroundColor Cyan
    New-Item -ItemType Directory -Force -Path $releasesDir | Out-Null
    vpk download github --repoUrl $RepoUrl --outputDir $releasesDir --channel win
    if ($LASTEXITCODE -ne 0) { Write-Host "    (no prior releases - continuing)" -ForegroundColor DarkGray }
}

Write-Host "==> [2/3] Packing with Velopack..." -ForegroundColor Cyan
$packArgs = @(
    "pack",
    "--packId", "LabelPrinter",
    "--packTitle", "LabelPrinter",
    "--packVersion", $Version,
    "--packDir", $publishDir,
    "--mainExe", "LabelPrinter.exe",
    "--icon", $iconPath,
    "--runtime", "win-x64",
    "--shortcuts", "StartMenuRoot,Startup",
    "--outputDir", $releasesDir
)
if ($signed) {
    Write-Host "    signing with cert $SignThumbprint" -ForegroundColor DarkGray
    $packArgs += @("--signParams", "/sha1 $SignThumbprint /fd sha256")
}
vpk @packArgs
if ($LASTEXITCODE -ne 0) { throw "vpk pack failed." }

$setupExe = Join-Path $releasesDir "LabelPrinter-win-Setup.exe"
if ($signed) {
    $sig = Get-AuthenticodeSignature $setupExe
    if ($sig.SignerCertificate.Thumbprint -ne $SignThumbprint) { throw "Setup.exe is not signed with $SignThumbprint." }
}

if (-not $Publish) {
    Write-Host ""
    Write-Host "==> Done (local). Installer: $setupExe" -ForegroundColor Green
    return
}

Write-Host "==> [3/3] Uploading to $RepoUrl ..." -ForegroundColor Cyan
vpk upload github `
    --repoUrl $RepoUrl `
    --token $token `
    --outputDir $releasesDir `
    --releaseName "LabelPrinter $Version" `
    --tag "v$Version" `
    --merge `
    --publish true
if ($LASTEXITCODE -ne 0) { throw "vpk upload failed." }

# Attach install.ps1 and the public certificate to the same release, so
#   irm <repo>/releases/latest/download/install.ps1 | iex
# always gets the script, certificate and Setup.exe of the latest release.
Write-Host "==> Attaching install.ps1 and the certificate..." -ForegroundColor Cyan
$repoPath = ([Uri]$RepoUrl).AbsolutePath.Trim('/')
$headers = @{
    Authorization          = "Bearer $token"
    Accept                 = "application/vnd.github+json"
    "X-GitHub-Api-Version" = "2022-11-28"
}
$release = Invoke-RestMethod -Uri "https://api.github.com/repos/$repoPath/releases/tags/v$Version" -Headers $headers
$extraAssets = @(
    (Join-Path $repoRoot "install.ps1"),
    (Join-Path $PSScriptRoot "labelprinter-codesign.cer")
)
foreach ($file in $extraAssets) {
    $name = Split-Path -Leaf $file
    # Re-running a publish of the same version replaces this release's copy.
    foreach ($old in @($release.assets | Where-Object { $_.name -eq $name })) {
        Invoke-RestMethod -Method Delete -Uri $old.url -Headers $headers | Out-Null
    }
    Invoke-RestMethod -Method Post -Headers $headers -InFile $file -ContentType "application/octet-stream" `
        -Uri "https://uploads.github.com/repos/$repoPath/releases/$($release.id)/assets?name=$name" | Out-Null
    Write-Host "    $name" -ForegroundColor DarkGray
}

# Keep only this version's packages locally; the next publish re-syncs from GitHub.
Get-ChildItem $releasesDir -File -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -like 'LabelPrinter-*.nupkg' -and $_.Name -notlike "LabelPrinter-$Version-*" } |
    Remove-Item -Force -ErrorAction SilentlyContinue

Write-Host ""
Write-Host "==> Published LabelPrinter v$Version to $RepoUrl/releases/tag/v$Version" -ForegroundColor Green
