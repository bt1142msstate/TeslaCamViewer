# FFmpeg Runtime

TESLA Cam uses FFmpeg for stream-copy stitching and export.

The development machine may contain FFmpeg binaries under `Tools/ffmpeg/bin`, but those binaries are intentionally ignored by Git to keep the public repository source-focused and avoid committing large generated/runtime artifacts.

Recommended runtime source:

- https://github.com/BtbN/FFmpeg-Builds
- LGPL shared Windows x64 build

Expected layout for local development:

```text
Tools/
  ffmpeg/
    bin/
      ffmpeg.exe
      avcodec-*.dll
      avformat-*.dll
      avutil-*.dll
      ...
    LICENSE-FFmpeg-BtbN.txt
```

If FFmpeg is missing, the app can still scan and play clips, but stitching and export features will be unavailable.
