# SysPulse

CPU and memory monitor for the PowerToys Command Palette Dock: live usage in the Dock, a top-processes
page with kill / kill-tree, and a Windows notification when usage stays above a threshold.

## Install

Requires Windows 10 19041+ / Windows 11 and PowerToys with Command Palette.

1. Download `SysPulse.cer` and the package for your machine (`SysPulse_<version>_x64.msix` or
   `SysPulse_<version>_arm64.msix`) from the [latest release](https://github.com/xmsadik/SysPulse-CmdPal/releases/latest).
2. Trust the signing certificate once. The packages are signed with a self-signed certificate, so
   Windows needs it in the local machine's *Trusted People* store. In an **elevated** PowerShell:

   ```powershell
   Import-Certificate -FilePath .\SysPulse.cer -CertStoreLocation Cert:\LocalMachine\TrustedPeople
   ```

3. Install the package by double-clicking the `.msix`, or:

   ```powershell
   Add-AppxPackage .\SysPulse_1.0.0.0_x64.msix
   ```

4. In Command Palette, run **Reload** (or restart Command Palette), then pin SysPulse to the Dock.

To uninstall: `Get-AppxPackage SysPulse | Remove-AppxPackage`.

## Build

- `scripts/deploy.ps1`: dev loop, which builds and registers the unpackaged layout (requires Developer Mode).
- `scripts/package.ps1`: builds signed x64 + ARM64 MSIX packages into `dist/`.
- `scripts/backup-signing-cert.ps1`: exports the signing certificate to a password-protected `.pfx` backup.

See `TESTING.md` for the manual test checklist and `DECISIONS.md` for design notes.
