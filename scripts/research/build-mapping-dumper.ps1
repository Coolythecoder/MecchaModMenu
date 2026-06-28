param(
    [string]$RuntimeRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path,
    [string]$Configuration = "Release",
    [string]$Platform = "x64",
    [string]$OutDir = ""
)

$ErrorActionPreference = "Stop"

if (-not $OutDir) {
    $OutDir = Join-Path $RuntimeRoot ".build\research\mapping_dumper\bin"
}
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

$Solution = Join-Path $RuntimeRoot "third_party\UnrealMappingsDumper\UnrealMappingsDumper.sln"
if (-not (Test-Path $Solution -PathType Leaf)) {
    throw "UnrealMappingsDumper solution not found: $Solution"
}

function Get-VsDevCmd {
    $VsWhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
    if (-not (Test-Path $VsWhere)) { return "" }
    $VsInstall = & $VsWhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
    if (-not $VsInstall) { return "" }
    $VsDevCmd = Join-Path $VsInstall "Common7\Tools\VsDevCmd.bat"
    if (Test-Path $VsDevCmd) { return $VsDevCmd }
    return ""
}

function Quote-CmdArg([string]$Value) {
    if ($Value -match '^[A-Za-z0-9_./:=+\-\\]+$') {
        return $Value
    }
    return '"' + ($Value -replace '"', '\"') + '"'
}

function Invoke-MsBuild {
    param([string[]]$ArgsList)
    if (Get-Command msbuild.exe -ErrorAction SilentlyContinue) {
        & msbuild.exe @ArgsList
        if ($LASTEXITCODE -ne 0) { throw "msbuild failed with exit code $LASTEXITCODE" }
        return
    }

    $VsDevCmd = Get-VsDevCmd
    if (-not $VsDevCmd) {
        throw "msbuild.exe was not found. Install Visual Studio 2022 Build Tools or run from a VS Developer PowerShell."
    }

    $ArgText = ($ArgsList | ForEach-Object { Quote-CmdArg $_ }) -join " "
    cmd /d /c "$(Quote-CmdArg $VsDevCmd) -arch=x64 -host_arch=x64 >nul && msbuild.exe $ArgText"
    if ($LASTEXITCODE -ne 0) { throw "msbuild failed with exit code $LASTEXITCODE" }
}

$OutDirWithSlash = (Resolve-Path $OutDir).Path.TrimEnd("\", "/") + "\"
Invoke-MsBuild @(
    $Solution,
    "/m",
    "/t:Build",
    "/p:Configuration=$Configuration",
    "/p:Platform=$Platform",
    "/p:OutDir=$OutDirWithSlash"
)

$Dll = Join-Path $OutDir "UnrealMappingsDumper.dll"
if (-not (Test-Path $Dll -PathType Leaf)) {
    throw "Dumper DLL was not produced: $Dll"
}

Write-Host "Built mapping dumper:"
Write-Host "  $Dll"
