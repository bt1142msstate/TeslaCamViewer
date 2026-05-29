[CmdletBinding()]
param(
    [string]$Repository = 'bt1142msstate/TeslaCamViewer',
    [string]$InstallDir = (Join-Path $env:LOCALAPPDATA 'Programs\TESLA Cam'),
    [string]$ReleaseTag,
    [switch]$StableOnly,
    [switch]$NoDesktopShortcut,
    [switch]$NoLaunch
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0

function Write-Step {
    param([string]$Message)
    Write-Host "==> $Message"
}

function Get-SafeInstallPath {
    param([string]$Path)

    $programsRoot = [System.IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA 'Programs'))
    $programsPrefix = $programsRoot.TrimEnd('\') + '\'
    $fullPath = [System.IO.Path]::GetFullPath($Path)
    $fullPrefix = $fullPath.TrimEnd('\') + '\'

    if (-not $fullPrefix.StartsWith($programsPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "InstallDir must be under $programsRoot for this installer."
    }

    if ($fullPrefix -eq $programsPrefix) {
        throw 'InstallDir must name an app folder, not the Programs root.'
    }

    return $fullPath
}

function New-AppShortcut {
    param(
        [string]$ShortcutPath,
        [string]$TargetPath,
        [string]$WorkingDirectory
    )

    $shortcutDirectory = Split-Path -Path $ShortcutPath -Parent
    New-Item -ItemType Directory -Path $shortcutDirectory -Force | Out-Null

    $shell = New-Object -ComObject WScript.Shell
    $shortcut = $shell.CreateShortcut($ShortcutPath)
    $shortcut.TargetPath = $TargetPath
    $shortcut.WorkingDirectory = $WorkingDirectory
    $shortcut.IconLocation = "$TargetPath,0"
    $shortcut.Description = 'TESLA Cam'
    $shortcut.Save()
}

function Get-ReleaseAsset {
    param(
        [string]$RepositoryName,
        [string]$TagName,
        [bool]$StableOnlyBuild
    )

    $headers = @{
        'Accept' = 'application/vnd.github+json'
        'User-Agent' = 'TESLA-Cam-Installer'
    }

    if ([string]::IsNullOrWhiteSpace($TagName)) {
        $releasesUrl = "https://api.github.com/repos/$RepositoryName/releases"
        $releases = Invoke-RestMethod -Uri $releasesUrl -Headers $headers
        $release = $releases |
            Where-Object { -not $_.draft -and (-not $StableOnlyBuild -or -not $_.prerelease) } |
            Select-Object -First 1
    }
    else {
        $releaseUrl = "https://api.github.com/repos/$RepositoryName/releases/tags/$TagName"
        $release = Invoke-RestMethod -Uri $releaseUrl -Headers $headers
        if ($release.draft) {
            throw "Release $TagName is a draft and cannot be installed."
        }

        if ($StableOnlyBuild -and $release.prerelease) {
            throw "Release $TagName is marked as pre-release. Run without -StableOnly to install it."
        }
    }

    if (-not $release) {
        throw "No downloadable GitHub release was found for $RepositoryName."
    }

    $asset = $release.assets |
        Where-Object { $_.name -eq 'TESLA-Cam-win-x64-portable.zip' -or $_.name -like '*win-x64*portable*.zip' } |
        Select-Object -First 1

    if (-not $asset) {
        throw "Release $($release.tag_name) does not contain a Windows portable zip asset."
    }

    return [pscustomobject]@{
        Release = $release
        Asset = $asset
    }
}

$installPath = Get-SafeInstallPath -Path $InstallDir
$tempRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("tesla-cam-install-$([System.Guid]::NewGuid().ToString('N'))")

try {
    if ([string]::IsNullOrWhiteSpace($ReleaseTag)) {
        Write-Step "Finding latest GitHub release for $Repository"
    }
    else {
        Write-Step "Finding GitHub release $ReleaseTag for $Repository"
    }
    $releaseAsset = Get-ReleaseAsset -RepositoryName $Repository -TagName $ReleaseTag -StableOnlyBuild ([bool]$StableOnly)
    $release = $releaseAsset.Release
    $asset = $releaseAsset.Asset

    Write-Step "Downloading $($asset.name) from release $($release.tag_name)"
    New-Item -ItemType Directory -Path $tempRoot -Force | Out-Null
    $zipPath = Join-Path $tempRoot $asset.name
    Invoke-WebRequest -Uri $asset.browser_download_url -OutFile $zipPath -Headers @{ 'User-Agent' = 'TESLA-Cam-Installer' }

    if (($asset.PSObject.Properties.Name -contains 'digest') -and ($asset.digest -like 'sha256:*')) {
        Write-Step 'Verifying package SHA-256'
        $expectedHash = $asset.digest.Substring('sha256:'.Length).ToUpperInvariant()
        $actualHash = (Get-FileHash -Algorithm SHA256 -Path $zipPath).Hash.ToUpperInvariant()
        if ($actualHash -ne $expectedHash) {
            throw "Package hash mismatch. Expected $expectedHash but got $actualHash."
        }
    }

    Write-Step 'Extracting package'
    $extractPath = Join-Path $tempRoot 'extract'
    Expand-Archive -Path $zipPath -DestinationPath $extractPath -Force

    $appExe = Get-ChildItem -Path $extractPath -Filter TeslaCamViewer.exe -Recurse | Select-Object -First 1
    if (-not $appExe) {
        throw 'Downloaded package does not contain TeslaCamViewer.exe.'
    }

    $payloadPath = $appExe.Directory.FullName
    $stagingPath = "$installPath.new"
    $backupPath = "$installPath.old"

    Write-Step "Installing to $installPath"
    Get-Process -Name TeslaCamViewer -ErrorAction SilentlyContinue | Stop-Process -Force
    Remove-Item -Path $stagingPath -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item -Path $backupPath -Recurse -Force -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Path $stagingPath -Force | Out-Null
    Copy-Item -Path (Join-Path $payloadPath '*') -Destination $stagingPath -Recurse -Force

    if (Test-Path $installPath) {
        Move-Item -Path $installPath -Destination $backupPath -Force
    }

    Move-Item -Path $stagingPath -Destination $installPath -Force
    Remove-Item -Path $backupPath -Recurse -Force -ErrorAction SilentlyContinue

    $installedExe = Join-Path $installPath 'TeslaCamViewer.exe'
    $startMenuShortcut = Join-Path ([Environment]::GetFolderPath('Programs')) 'TESLA Cam.lnk'
    New-AppShortcut -ShortcutPath $startMenuShortcut -TargetPath $installedExe -WorkingDirectory $installPath

    if (-not $NoDesktopShortcut) {
        $desktopShortcut = Join-Path ([Environment]::GetFolderPath('DesktopDirectory')) 'TESLA Cam.lnk'
        New-AppShortcut -ShortcutPath $desktopShortcut -TargetPath $installedExe -WorkingDirectory $installPath
    }

    Write-Step 'Install complete'
    Write-Host "Installed release: $($release.tag_name)"
    Write-Host "App path: $installedExe"

    if (-not $NoLaunch) {
        Start-Process -FilePath $installedExe -WorkingDirectory $installPath
    }
}
finally {
    Remove-Item -Path $tempRoot -Recurse -Force -ErrorAction SilentlyContinue
}
