# Architecture

## Overview

TESLA Cam is a WinUI 3 desktop application that scans TeslaCam media, groups raw files into drive sessions, presents them through a virtual stitched playback timeline, displays telemetry, and exports selected ranges.

The app is local-first. Video files, extracted archives, generated stitch/export artifacts, telemetry parsing, and exports are handled on the user's machine unless a future opt-in cloud visual-context feature is enabled.

## Repository Layout

- `src/TeslaCamViewer/` - main WinUI 3 app project.
- `src/TeslaCamViewer.Cleanup/` - standalone cleanup helper launched after app exit.
- `src/TeslaCamViewer/Assets/` - app icon, Store tile images, and splash assets.
- `src/TeslaCamViewer/Tools/ffmpeg/` - FFmpeg runtime metadata; local binaries live in ignored `bin/`.
- `docs/` - GitHub Pages site, screenshots, project docs, and legal notices.
- `scripts/` - GitHub Release install and uninstall scripts.
- `packaging/` - future package manager manifests and packaging notes.
- `.github/` - issue templates and CI/release workflows.

Root files are kept for GitHub discoverability and release/legal workflows:

- `README.md`
- `INSTALL.md`
- `LICENSE`
- `PRIVACY.md`
- `CONTRIBUTING.md`

## Projects

### Main App

Project: `src/TeslaCamViewer/TeslaCamViewer.csproj`

Key files:

- `src/TeslaCamViewer/App.xaml`
- `src/TeslaCamViewer/App.xaml.cs`
- `src/TeslaCamViewer/MainWindow.xaml`
- `src/TeslaCamViewer/MainWindow.xaml.cs`
- `src/TeslaCamViewer/CrashLogger.cs`
- `src/TeslaCamViewer/app.manifest`
- `src/TeslaCamViewer/Package.appxmanifest`

The current implementation is intentionally simple at the project level: most app behavior lives in `MainWindow.xaml.cs` while the product surface stabilizes. Future refactors should extract services only when they reduce real complexity or make test seams useful.

### Cleanup Helper

Project: `src/TeslaCamViewer.Cleanup/TeslaCamViewer.Cleanup.csproj`

The cleanup helper can run after app exit and delete old cached stitch/export files without blocking shutdown. The main app builds it as part of `BuildCleanupHelper` and copies its output under `Tools/cleanup/` in the app output folder.

## UI Shell

Responsibilities:

- Window setup and acrylic styling.
- Sidebar source selection, category tabs, search, and virtualized clip list.
- Multi-camera playback layout.
- Timeline scrubber, marker controls, export menu, and telemetry HUD.
- User-facing status, loading, virtual playback, stitching/export, and export progress indicators.

## Source Scanner

Primary code: `src/TeslaCamViewer/MainWindow.xaml.cs`

Responsibilities:

- Scan a mounted TeslaCam folder or copied TeslaCam directory.
- Watch folder sources for new, changed, deleted, or renamed MP4/event files.
- Extract supported ZIP archives into the local import cache.
- Detect normal TeslaCam layouts inside extracted archives.
- Fall back to the app's exported ZIP layout when the archive contains one MP4 per camera view.
- Build `TeslaClip` objects from `TeslaClipSegment` camera dictionaries.

## Playback Model

Types:

- `TeslaClip`
- `TeslaClipSegment`

Responsibilities:

- Represent a drive or event clip.
- Track camera paths by camera role.
- Estimate segment durations before media players report exact durations.
- Maintain the active virtual playback segment list, exact per-camera timeline, and cached stitched camera files for smoother multi-segment review.

## Stitching And Cache

Responsibilities:

- Watch-time playback starts from the raw segment list with exact MP4-derived durations so review does not block on FFmpeg concatenation. When cached stitched camera files exist or finish in the background, playback can switch to those single-file camera streams for smoother long-drive review.
- Use bundled FFmpeg for stream-copy concatenation when exporting or generating explicit stitch artifacts.
- Cache generated stitch/export artifacts under local app data when they are created.
- Keep virtual playback as the immediate fallback and primary review path.
- Clean temporary stitch/export folders.

FFmpeg metadata is tracked under `src/TeslaCamViewer/Tools/ffmpeg/`. Actual FFmpeg binaries are ignored and populated locally or by the release workflow.

## Telemetry Parser

Primary code: `TeslaSeiParser` in `src/TeslaCamViewer/MainWindow.xaml.cs`

Responsibilities:

- Parse MP4 sample timing and SEI NAL units.
- Decode embedded Tesla telemetry fields.
- Normalize telemetry offsets against clip duration.
- Feed speed, steering, gear, pedals, blinkers, GPS, heading, and autonomy state into the HUD.

## Export Pipeline

Responsibilities:

- Use IN and OUT markers to build per-segment export slices.
- Export the current main view to one MP4.
- Export all available views as multiple MP4 files inside a ZIP archive.
- Preserve video streams by using FFmpeg stream copy where possible.
- Validate exported telemetry after export and report whether front-camera telemetry was preserved.

## Planned Visual Context Layer

Planned components:

- Frame sampler.
- Representative clip photos for clip-list previews.
- Gallery-style clip browsing mode.
- Local visual index.
- Metadata store.
- Query layer.
- Optional telemetry overlays for preview cards, off by default so thumbnails are not covered.
- Optional Gemini-backed analyzer for subscription users.
- Category and flagging engine.

The intended design is local-first. Cloud visual context must be opt-in and scoped to user-selected clips or ranges.
Local visual context should detect suitable GPU or NPU hardware before enabling
on-device analysis. Users without suitable hardware should be able to keep using
the free core viewer and may later opt into cloud-based analysis through a
subscription path.

## Data Storage

Current local storage:

- Generated stitch/export cache: `%LOCALAPPDATA%\TeslaCamViewer`
- Archive import cache: `%LOCALAPPDATA%\TeslaCamViewer\imports`
- Local crash log in the app directory during development.

Future storage:

- Local visual index database.
- User preferences for layout and themes.
- Optional encrypted token store for Tesla API integration.

## Packaging

The development build is currently unpackaged. Store publication is planned through MSIX packaging with `src/TeslaCamViewer/Package.appxmanifest` and Store-owned package identity from Partner Center.

GitHub Release packaging is handled by `.github/workflows/release.yml`. It builds `src/TeslaCamViewer/TeslaCamViewer.csproj`, downloads FFmpeg into `src/TeslaCamViewer/Tools/ffmpeg/bin`, stages the app output, copies root release docs plus `docs/legal/THIRD-PARTY-NOTICES.txt`, and publishes:

- a Velopack-generated one-click Windows setup executable for the public site;
- the portable ZIP for manual installs;
- PowerShell install/uninstall fallback scripts;
- SHA-256 hashes for the downloadable assets.

The preview setup is intentionally treated as a GitHub direct-download bridge. The final consumer path should be Microsoft Store distribution or a signed installer once the final app name and publisher identity are ready.

Final Store submission still requires Partner Center identity values, final artwork, Store listing metadata, and certification validation.
