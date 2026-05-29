# Roadmap

This roadmap describes intended product direction. It is not a promise that every item will ship in every build.

## Near Term

- Finish Microsoft Store packaging validation.
- Add a first-run privacy and local-media explanation.
- Improve archive import handling for more ZIP layouts.
- Add stronger export progress reporting for multi-view exports.
- Add better error messages for missing FFmpeg, malformed TeslaCam folders, and corrupt clips.
- Replace prototype metadata labels with a more modern and consistent information layout.

## Visual Context

Visual context is planned as a major feature. The goal is to index local clips and create searchable video metadata.

Planned local visual context features:

- Local frame sampling and scene indexing.
- Local object, motion, and driving-event metadata.
- Ask questions about a clip or a drive, such as "when did a pedestrian appear?" or "show the moment the car changed lanes."
- Flag and separate clips into categories.
- Search by visual events, telemetry events, and time ranges.
- Keep local analysis available without requiring a subscription.

Planned optional subscription features for the free Windows Store version:

- Faster and more accurate visual context through Gemini.
- Cloud-assisted summaries and question answering over selected clips.
- Richer event categorization and metadata extraction.
- Optional Tesla API integrations for vehicle-aware context.

The subscription path must be opt-in and clearly explain what data is sent outside the device.

## Design

- More modern and intuitive navigation.
- Cleaner clip list density and sorting controls.
- Customizable design themes.
- Layout customization for camera views and telemetry cards.
- Better empty states, progress states, and import/export flows.

## Branding And Web

- Choose the final published app name.
- Develop a final app logo, icon set, and visual identity for the published
  product.
- Make the logo communicate drive review, camera footage, recording, and
  telemetry without implying Tesla affiliation or ownership of Tesla marks.
- Replace the current working icon assets with production-ready Windows Store
  artwork once the final name and logo are chosen.
- Create a GitHub Pages site for the app with screenshots, feature overview,
  download/store links, source-available license summary, privacy policy, and
  roadmap links.

## Tesla API

Tesla API usage is planned as an optional subscription feature. Possible uses:

- Vehicle metadata enrichment.
- Fleet or vehicle selection.
- Contextual data that improves clip organization.
- Future sync or association between recorded clips and vehicle state.

Tesla API support must be implemented with explicit user consent, secure token storage, and a clear privacy policy update.

## Publishing

The app is planned for eventual Microsoft Store publication under a final product
name that differs from this repository's working name. The Store version should
be free, with any subscription clearly separated from the core local viewer.

A macOS version is also planned. The Windows app remains the first target while
the viewer, import, stitching, telemetry, and export workflows stabilize.
