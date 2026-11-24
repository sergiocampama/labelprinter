# Encrypts the code-signing PFX with a passphrase (a random UUID) so it can be
# kept in the repo as a backup. Reverse with decrypt-pfx.ps1.
#
# File format (binary):
#   "LPENC1" (6) | salt (16) | iv (16) | AES-256-CBC ciphertext | HMAC-SHA256 (32)
# Keys: PBKDF2-HMAC-SHA256(passphrase, salt, 600000 iterations) -> 64 bytes;
# first 32 = AES key, last 32 = HMAC key. The HMAC covers everything before it
# (encrypt-then-MAC), so a wrong passphrase or a corrupted file is detected.
#
# Works in Windows PowerShell 5.1 and PowerShell 7.
#
# Example (export a PFX whose password is the same UUID, then encrypt):
#   $key  = [guid]::NewGuid().ToString()
#   $cert = Get-Item Cert:\CurrentUser\My\<thumbprint>
#   Export-PfxCertificate -Cert $cert -FilePath $env:TEMP\c.pfx -CryptoAlgorithmOption AES256_SHA256 `
#       -Password (ConvertTo-SecureString $key -AsPlainText -Force)
#   .\deploy\signing\encrypt-pfx.ps1 -PfxPath $env:TEMP\c.pfx -Key $key
#   Remove-Item $env:TEMP\c.pfx

param(
    [Parameter(Mandatory)] [string]$PfxPath,
    [Parameter(Mandatory)] [string]$Key,
    [string]$OutPath = (Join-Path $PSScriptRoot 'labelprinter-codesign.pfx.enc')
)

$ErrorActionPreference = 'Stop'

$plain = [IO.File]::ReadAllBytes((Resolve-Path $PfxPath))
$magic = [Text.Encoding]::ASCII.GetBytes('LPENC1')

$rng = [Security.Cryptography.RandomNumberGenerator]::Create()
$salt = New-Object byte[] 16; $rng.GetBytes($salt)
$iv = New-Object byte[] 16; $rng.GetBytes($iv)

$kdf = New-Object Security.Cryptography.Rfc2898DeriveBytes($Key, $salt, 600000, [Security.Cryptography.HashAlgorithmName]::SHA256)
$keys = $kdf.GetBytes(64)
$encKey = $keys[0..31]
$macKey = $keys[32..63]

$aes = [Security.Cryptography.Aes]::Create()
$aes.Mode = 'CBC'; $aes.Padding = 'PKCS7'
$aes.Key = [byte[]]$encKey; $aes.IV = $iv
$cipher = $aes.CreateEncryptor().TransformFinalBlock($plain, 0, $plain.Length)

$body = [byte[]]($magic + $salt + $iv + $cipher)
$mac = (New-Object Security.Cryptography.HMACSHA256(, [byte[]]$macKey)).ComputeHash($body)

[IO.File]::WriteAllBytes($OutPath, [byte[]]($body + $mac))
Write-Host "Encrypted $PfxPath -> $OutPath"
