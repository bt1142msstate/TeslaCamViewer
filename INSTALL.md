# Install From GitHub

The easiest preview install path is the one-click Windows setup executable from
GitHub Releases. It does not require Visual Studio, build tools, command-line
Git, or manual ZIP extraction.

Recommended preview download:

https://github.com/bt1142msstate/TeslaCamViewer/releases/download/v0.1.0-preview.3/TESLA-Cam-win-x64-Setup.exe

Download the setup executable and run it. It installs TESLA Cam under the user's
profile, creates Start Menu and desktop shortcuts, and launches the app. No
Visual Studio, build tools, Git clone, or command-line steps are required.

The long-term lowest-friction Windows path is Microsoft Store distribution. The
long-term command-line install path is WinGet, with GitHub Releases used as the
package source for release assets. That path will be enabled after the final
package name, signing, and Windows Package Manager manifest are ready.

Planned stable command:

```powershell
winget install --id BrandonTemple.FinalAppName -e
```

## Scripted Install Fallback

The PowerShell installer remains available for scripted installs. Download
`Install-TESLA-Cam.ps1` from the latest release:

https://github.com/bt1142msstate/TeslaCamViewer/releases

Then run:

```powershell
powershell -ExecutionPolicy Bypass -File .\Install-TESLA-Cam.ps1
```

To install a specific release tag:

```powershell
powershell -ExecutionPolicy Bypass -File .\Install-TESLA-Cam.ps1 -ReleaseTag v0.1.0-preview.3
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

Users who do not want to run the setup executable or installer script can
download the latest `TESLA-Cam-win-x64-portable.zip` from GitHub Releases,
extract it anywhere under their user profile, and run `TeslaCamViewer.exe`.

The portable zip includes the self-contained Windows app build and FFmpeg runtime
used for stitching and export.

## Uninstall

If you installed with the one-click setup, use Windows Settings > Apps >
Installed apps and uninstall TESLA Cam from there when the entry is present.

For scripted, portable, or full local cleanup, run this in PowerShell:

```powershell
$uninstaller = Join-Path $env:TEMP 'teslacam-uninstall.ps1'
Invoke-WebRequest 'https://raw.githubusercontent.com/bt1142msstate/TeslaCamViewer/main/scripts/uninstall.ps1' -OutFile $uninstaller
powershell -ExecutionPolicy Bypass -File $uninstaller
```

The cleanup script removes the fallback/script install folder, Start Menu and
desktop shortcuts, and local app data under:

```text
%LOCALAPPDATA%\TeslaCamViewer
```

That local data folder contains generated thumbnails, stitched playback cache,
archive import cache, and telemetry summary cache. To remove the app but keep
those local caches:

```powershell
powershell -ExecutionPolicy Bypass -File $uninstaller -KeepAppData
```

## Packaging Notes

The GitHub release package is not the final Microsoft Store distribution path.
Until Store signing or Authenticode signing is ready, Windows may warn that the
app was downloaded from the internet or is from an unknown publisher. The Store
version is planned to use normal signed install and update flow.

WinGet packaging should be submitted after the final app name and release
identity are chosen. The manifest should point directly at the GitHub Release
asset for the setup executable and include the release asset hash.
