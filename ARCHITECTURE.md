# Architecture

## Overview

TESLA Cam is a WinUI 3 desktop application that scans TeslaCam media, groups raw files into drive sessions, stitches camera streams for playback, displays telemetry, and exports selected ranges.

The app currently prioritizes local processing. Video files, extracted archives, stitched playback files, and exports are handled on the user's machine.

## Major Components

## UI Shell

Files:

- `App.xaml`
- `App.xaml.cs`
- `MainWindow.xaml`
- `MainWindow.xaml.cs`

Responsibilities:

- Window setup and acrylic styling.
- Sidebar source selection, category tabs, search, and virtualized clip list.
- Multi-camera playback layout.
- Timeline scrubber, marker controls, export menu, and telemetry HUD.
- User-facing status, loading, stitching, and export progress indicators.

## Source Scanner

Primary code lives in `MainWindow.xaml.cs`.

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
- Maintain the active stitched or raw playback segment list.

## Stitching And Cache

Responsibilities:

- Use bundled FFmpeg for stream-copy concatenation.
- Cache stitched camera files under local app data.
- Promote stitched playback when available.
- Fall back to raw segment playback when stitching is unavailable.
- Clean temporary stitch/export folders.

The cleanup helper is in `TeslaCamViewer.Cleanup`. It can run after app exit and delete old cached files without blocking app shutdown.

## Telemetry Parser

Primary code lives in `TeslaSeiParser` in `MainWindow.xaml.cs`.

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
- Local visual index.
- Metadata store.
- Query layer.
- Optional Gemini-backed analyzer for subscription users.
- Category and flagging engine.

The intended design is local-first. Cloud visual context must be opt-in and scoped to user-selected clips or ranges.

## Data Storage

Current local storage:

- Stitched cache: `%LOCALAPPDATA%\TeslaCamViewer`
- Archive import cache: `%LOCALAPPDATA%\TeslaCamViewer\imports`
- Local crash log in the app directory during development.

Future storage:

- Local visual index database.
- User preferences for layout and themes.
- Optional encrypted token store for Tesla API integration.

## Packaging

The development build is currently unpackaged. Store publication is planned through MSIX packaging with `Package.appxmanifest` and Store-owned package identity from Partner Center.

The repository includes MSIX scaffolding, but final Store submission still requires Partner Center identity values, final artwork, Store listing metadata, and certification validation.
