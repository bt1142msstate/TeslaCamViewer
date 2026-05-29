# Privacy

## Current App

TESLA Cam currently processes selected TeslaCam footage locally on the user's Windows device.

Current behavior:

- The app reads video files from a selected TeslaCam folder, copied folder, or ZIP archive.
- The app extracts selected ZIP archives into a local app cache.
- The app uses bundled FFmpeg locally for stitching and export.
- The app parses embedded telemetry locally.
- The app does not intentionally upload clips, telemetry, location data, or visual content.
- The app does not currently require an account.

Local data may include sensitive information such as GPS coordinates, driving routes, license plates, faces, and vehicle telemetry. Users should only open and export footage they have the right to use and share.

## Future Subscription Features

The roadmap includes optional cloud-assisted visual context using Gemini and optional Tesla API integration.

Before those features ship, the app must add:

- Clear opt-in consent before any clip or frame data leaves the device.
- A user-visible explanation of what data is sent, where it is processed, and why.
- Controls for deleting local visual indexes and cached cloud results.
- Secure token storage for any Tesla API connection.
- A published privacy policy URL suitable for Microsoft Store submission.

## Contact

For now, use the GitHub repository issue tracker once the public repository is available.
