param(
    [string]$RuntimeRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path,
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"

$Project = Join-Path $RuntimeRoot "tools\asset_probe\MecchaAssetProbe.csproj"
if (-not (Test-Path $Project -PathType Leaf)) {
    throw "Asset probe project not found: $Project"
}

dotnet build $Project -c $Configuration -p:TreatWarningsAsErrors=false -p:WarningsAsErrors=
if ($LASTEXITCODE -ne 0) {
    throw "dotnet build failed with exit code $LASTEXITCODE"
}
