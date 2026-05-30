[CmdletBinding()]
param(
    [string]$InstallDir = (Join-Path $env:LOCALAPPDATA 'Programs\TESLA Cam'),
    [string]$AppDataDir = (Join-Path $env:LOCALAPPDATA 'TeslaCamViewer'),
    [switch]$KeepAppData
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0

function Get-SafeInstallPath {
    param([string]$Path)

    $programsRoot = [System.IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA 'Programs'))
    $programsPrefix = $programsRoot.TrimEnd('\') + '\'
    $fullPath = [System.IO.Path]::GetFullPath($Path)
    $fullPrefix = $fullPath.TrimEnd('\') + '\'

    if (-not $fullPrefix.StartsWith($programsPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "InstallDir must be under $programsRoot for this uninstaller."
    }

    if ($fullPrefix -eq $programsPrefix) {
        throw 'InstallDir must name an app folder, not the Programs root.'
    }

    return $fullPath
}

function Get-SafeAppDataPath {
    param([string]$Path)

    $localAppDataRoot = [System.IO.Path]::GetFullPath($env:LOCALAPPDATA)
    $localAppDataPrefix = $localAppDataRoot.TrimEnd('\') + '\'
    $fullPath = [System.IO.Path]::GetFullPath($Path)
    $fullPrefix = $fullPath.TrimEnd('\') + '\'

    if (-not $fullPrefix.StartsWith($localAppDataPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "AppDataDir must be under $localAppDataRoot for this uninstaller."
    }

    if ($fullPrefix -eq $localAppDataPrefix) {
        throw 'AppDataDir must name an app data folder, not the LocalAppData root.'
    }

    return $fullPath
}

$installPath = Get-SafeInstallPath -Path $InstallDir
$appDataPath = Get-SafeAppDataPath -Path $AppDataDir
$startMenuShortcut = Join-Path ([Environment]::GetFolderPath('Programs')) 'TESLA Cam.lnk'
$desktopShortcut = Join-Path ([Environment]::GetFolderPath('DesktopDirectory')) 'TESLA Cam.lnk'

Get-Process -Name TeslaCamViewer -ErrorAction SilentlyContinue | Stop-Process -Force
Remove-Item -Path $startMenuShortcut -Force -ErrorAction SilentlyContinue
Remove-Item -Path $desktopShortcut -Force -ErrorAction SilentlyContinue
Remove-Item -Path $installPath -Recurse -Force -ErrorAction SilentlyContinue

if (-not $KeepAppData) {
    Remove-Item -Path $appDataPath -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Host "Removed TESLA Cam from $installPath"
if ($KeepAppData) {
    Write-Host "Kept local app data at $appDataPath"
}
else {
    Write-Host "Removed local app data from $appDataPath"
}
