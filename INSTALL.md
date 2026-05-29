# Install From GitHub

The long-term recommended command-line install path for Windows is WinGet, with
GitHub Releases used as the package source for release assets. That path will be
enabled after the final package name, signing, and Windows Package Manager
manifest are ready.

Planned stable command:

```powershell
winget install --id BrandonTemple.FinalAppName -e
```

Until that WinGet package exists, use the GitHub Release assets. Download
`Install-TESLA-Cam.ps1` from the latest release:

https://github.com/bt1142msstate/TeslaCamViewer/releases

Then run:

```powershell
powershell -ExecutionPolicy Bypass -File .\Install-TESLA-Cam.ps1
```

To install a specific release tag:

```powershell
powershell -ExecutionPolicy Bypass -File .\Install-TESLA-Cam.ps1 -ReleaseTag v0.1.0-preview.1
```

The installer downloads the selected non-draft GitHub release, verifies the zip
SHA-256 when GitHub provides a release asset digest, and extracts it to:

```text
%LOCALAPPDATA%\Programs\TESLA Cam
```

It also creates Start Menu and desktop shortcuts. No admin rights or Visual
Studio install are required.

Early builds may be marked as pre-release. To allow only stable releases once
stable releases exist:

```powershell
powershell -ExecutionPolicy Bypass -File .\Install-TESLA-Cam.ps1 -StableOnly
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

WinGet packaging should be submitted after the final app name and release
identity are chosen. The manifest should point directly at the GitHub Release
asset for the installer package and include the release asset hash.
