# LabelPrinter installer: trusts the LabelPrinter code-signing certificate on
# this machine, then downloads and runs the latest installer.
#
# Usage (paste into PowerShell):
#   irm https://github.com/sergiocampama/labelprinter/releases/latest/download/install.ps1 | iex
#
# Or with files you already have (e.g. a local build):
#   powershell -ExecutionPolicy Bypass -File install.ps1 -CertPath .\deploy\labelprinter-codesign.cer -SetupPath .\deploy\Releases\LabelPrinter-win-Setup.exe
#
# Every release carries this script, the certificate (public part only) and
# Setup.exe. The certificate is pinned by thumbprint, and Setup.exe only runs
# if it's signed with it. Only the certificate step elevates (UAC);
# LabelPrinter installs per user, so the installer itself runs as the current
# user. Keep this file ASCII-only: Windows PowerShell 5.1 decodes "irm"
# downloads without a charset as Latin-1.

param(
    # Use this certificate file instead of downloading it from the latest release.
    [string]$CertPath,
    # Use this installer instead of downloading it from the latest release.
    [string]$SetupPath
)

& {
    param([string]$CertPath, [string]$SetupPath)

    $ErrorActionPreference = 'Stop'
    $ProgressPreference = 'SilentlyContinue' # PS 5.1 downloads crawl with the progress bar
    [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12

    $Thumbprint = '9F9528D62D01C87ED4EABD1FCEBF2232C5A13F6A' # CN=LabelPrinter, O=LabelPrinter (expires 2031-11-23)
    $ReleaseUrl = 'https://github.com/sergiocampama/labelprinter/releases/latest/download'

    # Trusted = in both machine stores: Root (chain trust) + TrustedPublisher (publisher trust).
    function Test-CertTrusted {
        (Test-Path "Cert:\LocalMachine\Root\$Thumbprint") -and (Test-Path "Cert:\LocalMachine\TrustedPublisher\$Thumbprint")
    }

    Write-Host '==> LabelPrinter: certificado de firma' -ForegroundColor Cyan
    if (Test-CertTrusted) {
        Write-Host '    Ya es de confianza en este equipo.'
    }
    else {
        if ($CertPath) {
            $certBytes = [IO.File]::ReadAllBytes((Resolve-Path $CertPath).Path)
        }
        else {
            Write-Host '    Descargando el certificado...'
            $certBytes = (Invoke-WebRequest -Uri "$ReleaseUrl/labelprinter-codesign.cer" -UseBasicParsing).Content
        }
        $cert = [System.Security.Cryptography.X509Certificates.X509Certificate2]::new([byte[]]$certBytes)
        if ($cert.Thumbprint -ne $Thumbprint) {
            Write-Host "    Certificado inesperado ($($cert.Thumbprint)); no se instalara." -ForegroundColor Red
            return
        }
        $certBase64 = [Convert]::ToBase64String($cert.RawData)

        # Runs elevated: import the certificate into the LocalMachine stores.
        $import = @"
`$ErrorActionPreference = 'Stop'
`$cert = [System.Security.Cryptography.X509Certificates.X509Certificate2]::new([Convert]::FromBase64String('$certBase64'))
if (`$cert.Thumbprint -ne '$Thumbprint') { throw 'Certificado inesperado.' }
foreach (`$name in 'Root', 'TrustedPublisher') {
    `$store = [System.Security.Cryptography.X509Certificates.X509Store]::new(`$name, 'LocalMachine')
    `$store.Open('ReadWrite'); `$store.Add(`$cert); `$store.Close()
}
"@
        $isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
            [Security.Principal.WindowsBuiltInRole]::Administrator)
        if ($isAdmin) {
            Invoke-Expression $import
        }
        else {
            Write-Host '    Se pedira permiso de administrador (UAC) para instalarlo...'
            $encoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($import))
            try {
                Start-Process powershell.exe -Verb RunAs -Wait -WindowStyle Hidden `
                    -ArgumentList '-NoProfile', '-ExecutionPolicy', 'Bypass', '-EncodedCommand', $encoded
            }
            catch {
                Write-Host '    Cancelado: se necesita permiso de administrador.' -ForegroundColor Red
                return
            }
        }

        if (-not (Test-CertTrusted)) {
            Write-Host '    No se pudo instalar el certificado.' -ForegroundColor Red
            return
        }
        Write-Host '    Certificado instalado (Root + TrustedPublisher).' -ForegroundColor Green
    }

    $downloaded = $false
    if ($SetupPath) {
        $setup = (Resolve-Path $SetupPath).Path
    }
    else {
        Write-Host '==> LabelPrinter: descargando el instalador' -ForegroundColor Cyan
        $setup = Join-Path $env:TEMP 'LabelPrinter-Setup.exe'
        Invoke-WebRequest -Uri "$ReleaseUrl/LabelPrinter-win-Setup.exe" -OutFile $setup -UseBasicParsing
        $downloaded = $true
    }

    # Only run it if it's validly signed by the pinned certificate.
    $sig = Get-AuthenticodeSignature $setup
    if ($sig.Status -ne 'Valid' -or $sig.SignerCertificate.Thumbprint -ne $Thumbprint) {
        if ($downloaded) { Remove-Item $setup -Force -ErrorAction SilentlyContinue }
        Write-Host "    Firma invalida ($($sig.Status)); no se ejecutara." -ForegroundColor Red
        return
    }

    Write-Host '==> LabelPrinter: instalando...' -ForegroundColor Cyan
    # Wait for Setup.exe only. "Start-Process -Wait" in Windows PowerShell 5.1
    # also waits for child processes, and Setup starts the tray app, which
    # never exits, so the script would hang.
    $proc = Start-Process $setup -PassThru
    $null = $proc.Handle # keep the handle open, or ExitCode reads as empty afterwards
    $proc.WaitForExit()
    if ($downloaded) { Remove-Item $setup -Force -ErrorAction SilentlyContinue }
    if ($proc.ExitCode -ne 0) {
        # Setup has already shown why (e.g. not enough disk space); details in
        # %LocalAppData%\velopack\velopack.log.
        Write-Host "    El instalador fallo (codigo $($proc.ExitCode)). Detalles: $env:LOCALAPPDATA\velopack\velopack.log" -ForegroundColor Red
        return
    }
    Write-Host '    Listo. LabelPrinter quedo en la bandeja del sistema y se abrira al iniciar Windows.' -ForegroundColor Green
} $CertPath $SetupPath
