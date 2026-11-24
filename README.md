# LabelPrinter

Windows tray app for printing shipping labels on a USB thermal label printer
(ZPL, 203 dpi). It starts with Windows, lives in the system tray, and opens its
window when the tray icon is clicked. The interface is in Spanish.

## Install

Paste this into PowerShell:

```powershell
irm https://github.com/sergiocampama/labelprinter/releases/latest/download/install.ps1 | iex
```

The script comes from the latest release, which also holds the certificate and
Setup.exe. It:

1. Downloads the LabelPrinter code-signing certificate
   (`labelprinter-codesign.cer`, public part only), checks its thumbprint, and
   adds it to the machine's **Root** and **TrustedPublisher** stores. This
   step asks for administrator permission (UAC).
2. Downloads `LabelPrinter-win-Setup.exe`. It runs the installer only if the
   installer is signed with that certificate.

LabelPrinter installs per user in `%LocalAppData%\LabelPrinter`. It adds
shortcuts to the Start menu and the Startup folder, and updates itself from
this repo's releases.

## Using it

1. **Impresora**: the USB printers that are connected. The list refreshes when
   a printer is plugged in or unplugged.
2. **Tipo de documento**:
   - **Mercado Libre Full (100x150)**: Mercado Libre's ZPL label files
     (`.txt`). The label size is set in the job, so the printer's own size
     setting doesn't matter.
   - **Etiqueta PDF (100x150)**: PDF labels (Ninja Hubs, Blue Express, ...).
     The app finds the printed area on each page, fits it to 100 × 150 mm
     (rotated if that fits better), and sends it as a ZPL image.
3. **Archivo**: pick the file, then **Vista previa** to check it, or
   **Imprimir** to print it.

The app writes straight to the printer's USB interface, not through the Windows
print queue. The queue's XPS processor cancels raw jobs on some drivers.
Settings live in `%AppData%\LabelPrinter\settings.json`.

## Building

Requirements: the .NET 10 SDK and `dotnet tool install --global vpk --version 1.2.0`.

```powershell
dotnet run
```

The app ships as **win-x64** self-contained. Windows 11 on ARM runs it through
x64 emulation. Don't switch it to x86: under .NET 10 x86, SkiaSharp (used by
the ZPL preview) draws every `^FB` text field blank.

## Releasing

1. The signing certificate must be in `Cert:\CurrentUser\My` (thumbprint
   `9F9528D62D01C87ED4EABD1FCEBF2232C5A13F6A`). On a new machine, restore it
   from `deploy/signing/labelprinter-codesign.pfx.enc` with
   `deploy/signing/decrypt-pfx.ps1`, which asks for the recovery UUID kept
   outside the repo.
2. Store a GitHub token with *Contents: read and write* on this repo, in your
   own PowerShell window:
   ```powershell
   ./deploy/set-token.ps1
   ```
3. Build, sign and publish:
   ```powershell
   ./deploy/build-release.ps1 -Version 1.0.8 -Publish
   ```
   This publishes the app, packs it with Velopack (signed), and uploads the
   release with `install.ps1` and `labelprinter-codesign.cer` attached. Leave
   off `-Publish` for a local build in `deploy/Releases`.

Installed copies pick up the new version on their own.
