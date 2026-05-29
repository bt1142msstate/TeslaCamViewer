# WinGet Packaging Plan

WinGet should become the recommended command-line installation path after the
final app name, package identity, and release signing are ready.

Target user command:

```powershell
winget install --id BrandonTemple.FinalAppName -e
```

The package should point directly at the GitHub Release asset for the Windows
setup executable and include the asset SHA-256. The preview site now uses a
Velopack-generated setup executable for direct downloads, but the public WinGet
submission should wait until:

- the final product name is selected;
- the app has a stable package identifier;
- the GitHub Release package is signed or replaced with a signed installer;
- install and uninstall are silent and reliable;
- Microsoft Store/trademark wording and privacy URLs are settled.

Submission outline:

1. Install the manifest creator:

   ```powershell
   winget install wingetcreate
   ```

2. Create the manifest from the GitHub Release asset URL.
3. Validate the manifest locally with `winget validate`.
4. Test with `winget install --manifest <path>`.
5. Submit the manifest to the Windows Package Manager Community Repository.

Microsoft's Windows Package Manager validation checks that the installer URL is
accurate, the installer can run without prompts, install/uninstall behavior works
for users, and the package passes safety validation.
