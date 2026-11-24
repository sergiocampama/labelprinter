# Recovers the LabelPrinter code-signing PFX from labelprinter-codesign.pfx.enc
# (made by encrypt-pfx.ps1). The passphrase is the recovery UUID kept outside
# the repo; the decrypted PFX's own password is that same UUID.
#
# Usage (Windows PowerShell 5.1 or PowerShell 7), from the repo root:
#   .\deploy\signing\decrypt-pfx.ps1                  # prompts for the UUID
#
# Then import it into the user store (build-release.ps1 signs by thumbprint
# from Cert:\CurrentUser\My) and delete the decrypted file:
#   Import-PfxCertificate -FilePath $env:USERPROFILE\.labelprinter\labelprinter-codesign-restored.pfx `
#       -CertStoreLocation Cert:\CurrentUser\My -Exportable `
#       -Password (Read-Host 'UUID' -AsSecureString)
#
# Expected certificate: CN=LabelPrinter, O=LabelPrinter; the thumbprint is the
# default -SignThumbprint in deploy/build-release.ps1. *.pfx is gitignored.

param(
    [string]$Key,
    [string]$InPath = (Join-Path $PSScriptRoot 'labelprinter-codesign.pfx.enc'),
    [string]$OutPath = (Join-Path $env:USERPROFILE '.labelprinter\labelprinter-codesign-restored.pfx'),
    [switch]$Force
)

$ErrorActionPreference = 'Stop'

if (-not $Key) {
    $secure = Read-Host 'UUID' -AsSecureString
    $Key = [Runtime.InteropServices.Marshal]::PtrToStringBSTR([Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure))
}
$Key = $Key.Trim()

if ((Test-Path $OutPath) -and -not $Force) {
    throw "$OutPath already exists (use -Force to overwrite)."
}

$data = [IO.File]::ReadAllBytes((Resolve-Path $InPath))
if ($data.Length -lt 6 + 16 + 16 + 16 + 32 -or [Text.Encoding]::ASCII.GetString($data, 0, 6) -ne 'LPENC1') {
    throw 'Not an LPENC1 file.'
}

$salt = $data[6..21]
$iv = $data[22..37]
$bodyLen = $data.Length - 32
$body = $data[0..($bodyLen - 1)]
$mac = $data[$bodyLen..($data.Length - 1)]
$cipher = $data[38..($bodyLen - 1)]

$kdf = New-Object Security.Cryptography.Rfc2898DeriveBytes($Key, [byte[]]$salt, 600000, [Security.Cryptography.HashAlgorithmName]::SHA256)
$keys = $kdf.GetBytes(64)
$encKey = $keys[0..31]
$macKey = $keys[32..63]

# Verify before decrypting (constant-time compare).
$expected = (New-Object Security.Cryptography.HMACSHA256(, [byte[]]$macKey)).ComputeHash([byte[]]$body)
$diff = 0
for ($i = 0; $i -lt 32; $i++) { $diff = $diff -bor ($expected[$i] -bxor $mac[$i]) }
if ($diff -ne 0) {
    throw 'Wrong UUID or corrupted file.'
}

$aes = [Security.Cryptography.Aes]::Create()
$aes.Mode = 'CBC'; $aes.Padding = 'PKCS7'
$aes.Key = [byte[]]$encKey; $aes.IV = [byte[]]$iv
$plain = $aes.CreateDecryptor().TransformFinalBlock([byte[]]$cipher, 0, $cipher.Count)

$dir = Split-Path $OutPath -Parent
if ($dir) { New-Item -ItemType Directory -Force -Path $dir | Out-Null }
[IO.File]::WriteAllBytes($OutPath, $plain)
Write-Host "Decrypted -> $OutPath (its PFX password is the same UUID)"
