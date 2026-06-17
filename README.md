<div align="center">
  <img src="src/TeslaCamViewer/Assets/AppIcon.svg" width="86" alt="TESLA Cam app icon">
  <h1>TESLA Cam</h1>
  <p><strong>Free source-available Windows TeslaCam viewer for exact-timed virtual stitched drive playback, multi-camera review, telemetry indexing, manual-driving timeline ranges, and export.</strong></p>
  <p>
    <a href="https://github.com/bt1142msstate/TeslaCamViewer/actions/workflows/windows-build.yml"><img alt="Windows Build" src="https://img.shields.io/github/actions/workflow/status/bt1142msstate/TeslaCamViewer/windows-build.yml?branch=main&label=windows%20build"></a>
    <a href="https://github.com/bt1142msstate/TeslaCamViewer/releases"><img alt="GitHub Release" src="https://img.shields.io/github/v/release/bt1142msstate/TeslaCamViewer?include_prereleases&label=release"></a>
    <img alt="Platform" src="https://img.shields.io/badge/platform-Windows%2010%2B-0078D4">
    <img alt="Built with WinUI 3" src="https://img.shields.io/badge/UI-WinUI%203-5AC8FA">
    <img alt="License" src="https://img.shields.io/badge/license-source--available-informational">
  </p>
</div>

![TESLA Cam playback dashboard with virtual stitched TeslaCam cameras and telemetry](docs/screenshots/app-playback-dashboard.png)

TESLA Cam is a Windows desktop viewer for TeslaCam footage. It is built with WinUI 3 and Windows App SDK, and focuses on turning Tesla's split camera files into a drive-centered viewing experience with exact MP4-derived timing, fast virtual continuous playback, synchronized telemetry, searchable clip context, and export tools.

The core app is intended to stay free. The free version should not add ads,
promotional overlays, export watermarks, or forced branding to your clips.
Future subscriptions and donations may support cloud-assisted features,
including visual context for devices without suitable GPU or NPU hardware,
development, signing, hosting, testing, and upkeep, but they should be optional.

This project is not affiliated with, endorsed by, or sponsored by Tesla, Inc. Tesla and related vehicle names are trademarks of their respective owners.

The public repository uses a working project name. The published app is planned
to ship under a different final name, and a macOS version is planned after the
Windows app is further along. A public GitHub Pages preview site is available at
https://bt1142msstate.github.io/TeslaCamViewer/ and should be refreshed once the
final name, logo, screenshots, and privacy URL are ready.

Project owner: [Brandon Temple](https://brandontemple.com/). Contact links are
available on the [portfolio contact section](https://brandontemple.com/#contact).

## Screenshots

![TESLA Cam drive scan and clip list](docs/screenshots/app-drive-overview.png)

## Categories And Tags

Categories: TeslaCam viewer, dashcam footage review, multi-camera video player,
vehicle telemetry, Windows desktop app, exact-timed virtual video playback, clip
export, marker based editing, manual driving timeline review, Sentry Mode
review.

Tags: `tesla`, `teslacam`, `tesla-dashcam`, `dashcam-viewer`, `winui3`,
`windows-app-sdk`, `dotnet`, `ffmpeg`, `mp4`, `telemetry`, `fsd`,
`manual-driving-ranges`, `exact-video-timing`, `virtual-playback`,
`fast-drive-review`, `video-stitching`, `video-export`, `sentry-mode`,
`source-available`, `github-releases`, `velopack`, `winget`.

## Current Features

- Scan a mounted TeslaCam drive, copied TeslaCam folder, or supported ZIP archive.
- Watch the selected source for detached drives, changed folders, and newly added clips.
- Group TeslaCam segments into exact-duration drive sessions using MP4 sample and edit table timing.
- Play each drive immediately through the raw segment timeline, then switch to cached stitched camera files when they are available for smoother long-drive playback.
- Show front, rear, repeater, and pillar camera views together in a dark translucent WinUI interface.
- Swap the main camera view by clicking an auxiliary view.
- Scrub across the full drive timeline with a custom liquid-glass scrubber.
- Show yellow manual-driving ranges directly on the timeline track.
- Add IN and OUT range markers for exporting.
- Display embedded telemetry including speed, steering, gear, pedals, blinkers, GPS, heading, g-force, and autonomy state.
- Label steering as left, right, or straight instead of showing negative steering values.
- Index clip-level FSD percentage and disengagement counts in the background.
- Use exact per-camera MP4 durations for timeline starts, telemetry windows, manual-driving ranges, virtual playback, stitching, and export slices.
- Cache telemetry summaries locally so repeat scans are faster without writing to the Tesla storage device.
- Show app status while scanning, indexing telemetry, loading virtual playback, and exporting.
- Show a top-right update status icon with current version, release notes, current feature coverage, and one-click update actions.
- Use a virtualized clip list so large drives do not spawn every clip row at once.
- Switch to collage mode with first-frame thumbnails for faster visual scanning.
- Resize the sidebar to give collage mode more room.
- Export either the current camera view as MP4 or all views as a compressed ZIP package.
- Validate post-export telemetry preservation.
- Use local caches and a background cleanup helper for generated stitch/export artifacts, archive imports, thumbnails, and telemetry summaries.

## Windows Support

- Windows 10 version 1809 or newer.
- x64 builds.
- Windows App SDK / WinUI 3 desktop app.
- Default local developer build is unpackaged.
- Microsoft Store packaging is planned through MSIX.

## Install From GitHub

Users do not need Visual Studio to try a published GitHub release. The easiest
preview path is the one-click Windows setup from the project site. The long-term
lowest-friction path is Microsoft Store distribution, with WinGet planned as the
recommended command-line option after the final package name, signing, and
Windows Package Manager manifest are ready:

```powershell
winget install --id BrandonTemple.FinalAppName -e
```

Current preview release assets:

- [Download Windows setup (recommended)](https://github.com/bt1142msstate/TeslaCamViewer/releases/download/v0.1.0-preview.6/TESLA-Cam-win-x64-Setup.exe)
- [Download portable ZIP](https://github.com/bt1142msstate/TeslaCamViewer/releases/download/v0.1.0-preview.6/TESLA-Cam-win-x64-portable.zip)
- [Download PowerShell fallback script](https://github.com/bt1142msstate/TeslaCamViewer/releases/download/v0.1.0-preview.6/Install-TESLA-Cam.ps1)
- [Download cleanup/uninstall script](https://github.com/bt1142msstate/TeslaCamViewer/releases/download/v0.1.0-preview.6/Uninstall-TESLA-Cam.ps1)
- [View checksums](https://github.com/bt1142msstate/TeslaCamViewer/releases/download/v0.1.0-preview.6/SHA256SUMS.txt)

The setup installer installs the app under the user's profile and creates app
shortcuts without requiring Visual Studio, build tools, Git, or manual
extraction. The portable ZIP and PowerShell scripts remain available for manual,
scripted, and full-clean uninstall flows. The cleanup script can remove local
app data including generated stitch/export artifacts, archive imports, thumbnails, and
telemetry summary cache when a full uninstall is desired. See [INSTALL.md](INSTALL.md)
for preview install, manual install, stable-only install, and uninstall details.

## Build

Open the project in Visual Studio 2026 or newer with the Windows App SDK workload installed, then build `src/TeslaCamViewer/TeslaCamViewer.csproj` for `x64`.

Command-line build used during development:

```powershell
& 'C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\amd64\MSBuild.exe' src\TeslaCamViewer\TeslaCamViewer.csproj /t:Build /p:Configuration=Release /p:Platform=x64 /v:minimal
```

## Repository Layout

- `src/TeslaCamViewer/` - WinUI 3 desktop app, package assets, manifests, and bundled runtime tool metadata.
- `src/TeslaCamViewer.Cleanup/` - background cleanup helper used after app exit.
- `docs/` - GitHub Pages site, public screenshots, project docs, and legal notices.
- `scripts/` - install and uninstall scripts used by GitHub Releases.
- `packaging/` - future package manager manifests and packaging notes.
- `.github/` - issue templates and GitHub Actions workflows.

## Microsoft Store Plan

The Windows Store version is planned to be free and published under a final
name that differs from this repository name. The free version should remain
useful without a subscription and should not add ads, promotional overlays,
export watermarks, or forced branding. A future optional subscription may add
faster and more accurate cloud-assisted visual context using Gemini, plus Tesla
API powered features, including cloud analysis for devices without suitable GPU
or NPU hardware. Donations may also be offered for people who want to
support development and upkeep.

See [STORE_READINESS.md](docs/project/STORE_READINESS.md) for the current packaging and certification checklist.

## Roadmap

See [ROADMAP.md](docs/project/ROADMAP.md).

## Contributing

See [CONTRIBUTING.md](CONTRIBUTING.md). Do not attach private TeslaCam footage,
GPS traces, faces, license plates, or other sensitive media unless you are
comfortable making it public.

## Architecture

See [ARCHITECTURE.md](docs/project/ARCHITECTURE.md).

## Privacy

See [PRIVACY.md](PRIVACY.md). The current app works locally and does not intentionally upload clips or telemetry.

## License

This project is source-available, not open source. You may inspect, build, run,
and modify it for personal, non-commercial use. You may not sell, sublicense,
redistribute, publish modified builds, or present it as your own product without
written permission. See [LICENSE](LICENSE).
