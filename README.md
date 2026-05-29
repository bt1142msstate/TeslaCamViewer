# TESLA Cam

TESLA Cam is a Windows desktop viewer for TeslaCam footage. It is built with WinUI 3 and Windows App SDK, and focuses on turning Tesla's split camera files into a drive-centered viewing experience.

This project is not affiliated with, endorsed by, or sponsored by Tesla, Inc. Tesla and related vehicle names are trademarks of their respective owners.

The public repository uses a working project name. The published app is planned
to ship under a different final name, and a macOS version is planned after the
Windows app is further along. A public GitHub Pages site is planned for the
published app once the final name, logo, screenshots, and privacy URL are ready.

## Current Features

- Scan a mounted TeslaCam drive, copied TeslaCam folder, or supported ZIP archive.
- Group one-minute TeslaCam segments into drive sessions.
- Stitch each drive into continuous camera feeds for smoother playback.
- Show front, rear, repeater, and pillar camera views together.
- Swap the main camera view by clicking an auxiliary view.
- Display embedded telemetry including speed, steering, gear, pedals, blinkers, GPS, heading, and autonomy state.
- Scrub across the full drive timeline.
- Add IN and OUT markers for exporting.
- Export the current view as MP4.
- Export all views into a compressed ZIP folder.
- Validate post-export telemetry preservation.
- Use a local cache and background cleanup helper for stitched clips.

## Windows Support

- Windows 10 version 1809 or newer.
- x64 builds.
- Windows App SDK / WinUI 3 desktop app.
- Default local developer build is unpackaged.
- Microsoft Store packaging is planned through MSIX.

## Install From GitHub

Users do not need Visual Studio to try a published GitHub release. Run this in
PowerShell:

```powershell
$installer = Join-Path $env:TEMP 'teslacam-install.ps1'
Invoke-WebRequest 'https://raw.githubusercontent.com/bt1142msstate/TeslaCamViewer/main/scripts/install.ps1' -OutFile $installer
powershell -ExecutionPolicy Bypass -File $installer
```

This installs the latest non-draft GitHub release to
`%LOCALAPPDATA%\Programs\TESLA Cam` and creates app shortcuts. See
[INSTALL.md](INSTALL.md) for manual install, stable-only install, and uninstall
details.

## Build

Open the project in Visual Studio 2026 or newer with the Windows App SDK workload installed, then build `TeslaCamViewer.csproj` for `x64`.

Command-line build used during development:

```powershell
& 'C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\amd64\MSBuild.exe' TeslaCamViewer.csproj /t:Build /p:Configuration=Release /p:Platform=x64 /v:minimal
```

## Microsoft Store Plan

The Windows Store version is planned to be free and published under a final
name that differs from this repository name. A future optional subscription may
add faster and more accurate cloud-assisted visual context using Gemini, plus
Tesla API powered features. The free app should remain useful without a
subscription.

See [STORE_READINESS.md](STORE_READINESS.md) for the current packaging and certification checklist.

## Roadmap

See [ROADMAP.md](ROADMAP.md).

## Architecture

See [ARCHITECTURE.md](ARCHITECTURE.md).

## Privacy

See [PRIVACY.md](PRIVACY.md). The current app works locally and does not intentionally upload clips or telemetry.

## License

This project is source-available, not open source. You may inspect, build, run,
and modify it for personal, non-commercial use. You may not sell, sublicense,
redistribute, publish modified builds, or present it as your own product without
written permission. See [LICENSE](LICENSE).
