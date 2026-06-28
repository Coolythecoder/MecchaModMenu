param(
    [string]$RuntimeRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path,
    [string]$PaksPath = "",
    [string]$ExePath = "",
    [string]$UsmapPath = "",
    [string]$GameVersion = "",
    [int]$PackageLimit = 3000,
    [string]$ProfileOut = "",
    [int]$ExportTopSkeletal = 0,
    [string]$MeshOutDir = "",
    [switch]$AllowFailure
)

$ErrorActionPreference = "Stop"

if (-not $PaksPath) {
    $PaksPath = "C:\Program Files (x86)\Steam\steamapps\common\MECCHA CHAMELEON\Chameleon\Content\Paks"
}
if (-not $ExePath) {
    $ExePath = "C:\Program Files (x86)\Steam\steamapps\common\MECCHA CHAMELEON\Chameleon\Binaries\Win64\PenguinHotel-Win64-Shipping.exe"
}
if (-not $ProfileOut) {
    $ProfileOut = Join-Path $RuntimeRoot ".build\research\profiles\asset-probe-latest.json"
}
if (-not $MeshOutDir) {
    $MeshOutDir = Join-Path $RuntimeRoot ".build\research\mesh_exports"
}

$Project = Join-Path $RuntimeRoot "tools\asset_probe\MecchaAssetProbe.csproj"
$ArgsList = @(
    "--paks", $PaksPath,
    "--exe", $ExePath,
    "--limit", "$PackageLimit",
    "--profile-out", $ProfileOut,
    "--mesh-out-dir", $MeshOutDir
)
if ($UsmapPath) {
    $ArgsList += @("--usmap", $UsmapPath)
}
if ($GameVersion) {
    $ArgsList += @("--game", $GameVersion)
}
if ($ExportTopSkeletal -gt 0) {
    $ArgsList += @("--export-top-skeletal", "$ExportTopSkeletal")
}

dotnet run -c Release --project $Project -- @ArgsList
if ($LASTEXITCODE -ne 0 -and -not $AllowFailure) {
    throw "asset probe failed with exit code $LASTEXITCODE"
}
