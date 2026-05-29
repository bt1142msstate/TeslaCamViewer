# Install From GitHub

The GitHub install path is intended for users who want to try the app without
installing Visual Studio or building the source code.

Run this in PowerShell:

```powershell
$installer = Join-Path $env:TEMP 'teslacam-install.ps1'
Invoke-WebRequest 'https://raw.githubusercontent.com/bt1142msstate/TeslaCamViewer/main/scripts/install.ps1' -OutFile $installer
powershell -ExecutionPolicy Bypass -File $installer
```

The installer downloads the newest non-draft GitHub release, extracts it to:

```text
%LOCALAPPDATA%\Programs\TESLA Cam
```

It also creates Start Menu and desktop shortcuts. No admin rights or Visual
Studio install are required.

Early builds may be marked as pre-release. To install only stable releases once
stable releases exist:

```powershell
powershell -ExecutionPolicy Bypass -File $installer -StableOnly
```

## Manual Portable Install

Users who do not want to run the installer script can download the latest
`TESLA-Cam-win-x64-portable.zip` from GitHub Releases, extract it anywhere under
their user profile, and run `TeslaCamViewer.exe`.

The portable zip includes the self-contained Windows app build and FFmpeg runtime
used for stitching and export.

## Uninstall

Run this in PowerShell:

```powershell
$uninstaller = Join-Path $env:TEMP 'teslacam-uninstall.ps1'
Invoke-WebRequest 'https://raw.githubusercontent.com/bt1142msstate/TeslaCamViewer/main/scripts/uninstall.ps1' -OutFile $uninstaller
powershell -ExecutionPolicy Bypass -File $uninstaller
```

## Packaging Notes

The GitHub release package is not the final Microsoft Store distribution path.
Until Store signing is ready, Windows may warn that the app was downloaded from
the internet or is from an unknown publisher. The Store version is planned to use
normal signed install and update flow.
