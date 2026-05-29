# Microsoft Store Readiness

This file tracks the work needed before the Windows app is submitted to the
Microsoft Store. `TESLA Cam` is the current working project name; the published
app is planned to use a different final name.

Research date: May 29, 2026.

## Current Status

The app is source-ready for public development and has an MSIX packaging scaffold. It is not ready for final Microsoft Store submission until the final product name, Partner Center identity values, final listing assets, and certification validation are completed.

The planned Store app should be free. The free core viewer should not include
ads, promotional overlays, export watermarks, or forced branding. Any future
subscription or donation path should be optional and clearly separated from the
local viewing, telemetry, marker, and export workflows.

Verified on May 29, 2026:

- Release x64 build passes locally.
- Unsigned x64 MSIX package generation passes locally.
- Windows App Certification Kit reports `OVERALL_RESULT=PASS` for the generated unsigned local MSIX.
- GitHub Actions `Windows Build` passes on the public repository.
- Velopack one-click setup generation passes locally for the staged Release output.

The repository contains:

- `src/TeslaCamViewer/Package.appxmanifest`
- package logo PNG assets in `src/TeslaCamViewer/Assets/`
- GitHub Release packaging workflow, one-click setup packaging, and PowerShell install/uninstall scripts
- `src/TeslaCamViewer/Properties/launchSettings.json` with an MSIX launch profile
- package metadata in `src/TeslaCamViewer/TeslaCamViewer.csproj`
- privacy, roadmap, architecture, third-party notices, and license files

## Store Requirements From Microsoft Guidance

Microsoft's current Windows app publishing guidance says:

- Microsoft Store is the recommended path for most Windows apps because it handles signing, update delivery, discovery, and trusted install flow.
- Store submission runs through Partner Center, including account creation, app-name reservation, package upload, listing metadata, certification, and publishing.
- WinUI 3 Store submission should use MSIX/MSIX bundle packaging.
- MSIX packages submitted to the Store are re-signed by Microsoft during certification.
- Direct-download EXE/MSI distribution remains the app owner's responsibility for signing, hosting, updates, and SmartScreen/reputation handling.
- The `ms-appinstaller` URI protocol is disabled by default as of Microsoft's current guidance, so the site should not rely on web-click MSIX install as the primary preview path.
- Single-project MSIX is supported for WinUI 3 projects, but more complex packages may need a Windows Application Packaging Project.
- Store policy compliance and privacy disclosure are required, especially before subscriptions, cloud processing, personal data, or account integrations are added.

Sources:

- https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/publish-first-app
- https://learn.microsoft.com/en-us/windows/apps/windows-app-sdk/single-project-msix
- https://learn.microsoft.com/en-us/windows/apps/publish/store-policies
- https://learn.microsoft.com/en-us/windows/apps/distribute-through-store/how-to-distribute-your-win32-app-through-microsoft-store
- https://learn.microsoft.com/en-us/windows/package-manager/package/manifest
- https://support.microsoft.com/en-us/windows/app-browser-control-in-the-windows-security-app-8f68fb65-ebb4-3cfb-4bd7-ef0f376f3dc3
- https://docs.velopack.io/packaging/installer

## Open Store Tasks

- Choose and reserve the final app name in Partner Center.
- Choose the final logo and production icon set for the published app.
- Replace `src/TeslaCamViewer/Package.appxmanifest` identity values with Partner Center identity values.
- Build the Store upload package with Partner Center identity selected.
- Run Windows App Certification Kit again against the final Partner Center/Store identity package and save the report.
- Add code signing for non-Store GitHub release packages.
- Submit a WinGet manifest after the final app name, package identity, and
  release artifact URL are stable.
- Decide final Store route:
  - MSIX package with a packaging project if bundled helper executables require it.
  - Store-distributed Win32 installer if MSIX packaging is not practical.
- Produce final Store artwork, screenshots, and promotional images.
- Keep the GitHub Pages site current, then update it with final branding and
  Store links after publication.
- Keep GitHub Releases available as an interim no-Visual-Studio install path
  until Store distribution is ready. The current public-site bridge is a
  Velopack-generated setup executable, with portable ZIP and PowerShell fallback
  assets retained for manual installs.
- Produce Store listing copy, category, keywords, age rating, and support contact.
- Publish a stable privacy policy URL.
- Confirm FFmpeg LGPL notice and binary distribution are acceptable for the chosen package route.
- Confirm Tesla trademark wording in Store listing avoids implying affiliation or endorsement.
- Decide whether crash logging should move from the app folder to local app data for packaged builds.
- Add a clear first-run or settings privacy note before visual context or Tesla API features ship.
- Add subscription disclosures before any paid plan is offered.
- Add donation copy only if a donation path exists, and make clear that donating
  is optional support for development and upkeep.
- Verify final Store listing copy says the free version has no forced branding
  or export watermarks.

## Known Packaging Concerns

- The app currently bundles FFmpeg and a cleanup helper executable. Microsoft's single-project MSIX documentation notes limitations around packages with multiple executables. If Store MSIX packaging rejects this layout, use a Windows Application Packaging Project or adjust packaged builds to omit helper executables.
- The current `src/TeslaCamViewer/Package.appxmanifest` contains placeholder identity values. Partner Center values are authoritative.
- The current PNG assets are generated from the app icon and are acceptable for packaging tests, but should be replaced with final Store-quality artwork.

## Suggested Pre-Submission Command Checks

```powershell
& 'C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\amd64\MSBuild.exe' src\TeslaCamViewer\TeslaCamViewer.csproj /t:Build /p:Configuration=Release /p:Platform=x64 /v:minimal
```

Unsigned local MSIX package check:

```powershell
& 'C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\amd64\MSBuild.exe' src\TeslaCamViewer\TeslaCamViewer.csproj /t:Build /p:Configuration=Release /p:Platform=x64 /p:WindowsPackageType=MSIX /p:GenerateAppxPackageOnBuild=true /p:AppxBundle=Never /p:UapAppxPackageBuildMode=SideloadOnly /p:AppxPackageSigningEnabled=false /v:minimal
```

Package validation should be run from Visual Studio's packaging flow or MSBuild once the final Store packaging route is chosen.

The local WACK report generated during this pass returned overall PASS. It still included optional warnings from Windows Runtime metadata validation and process-launch API scanning, which are common review points for WinUI/.NET desktop packages and should be reviewed again when producing the final Store package.
