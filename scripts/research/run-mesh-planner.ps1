param(
    [string]$RuntimeRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path,
    [string]$MeshPath = "",
    [string]$LastStatusPath = "",
    [string]$FrontSamplesPath = "",
    [string]$OutPath = "",
    [string]$CameraDirection = "",
    [int]$TextureSize = 1024,
    [double]$SampleStepTexels = 16,
    [int]$MaxSamples = 20000
)

$ErrorActionPreference = "Stop"

if (-not $MeshPath) {
    $MeshPath = Join-Path $RuntimeRoot ".build\research\mesh_exports\paintman-Chameleon_Content_3Dmodel_cLeon_charactor_paintman_skeltal_paintman.uasset.lod0.json"
}
if (-not $LastStatusPath) {
    $LastStatusPath = Join-Path $env:LOCALAPPDATA "MecchaCamouflage\runtime\last_status.json"
}
if (-not $FrontSamplesPath) {
    $NativeDir = Join-Path $env:LOCALAPPDATA "MecchaCamouflage\runtime\native"
    if (Test-Path $NativeDir -PathType Container) {
        $LatestFrontSamples = Get-ChildItem -Path $NativeDir -Filter "*.front_samples.json" -File -ErrorAction SilentlyContinue |
            Sort-Object LastWriteTime -Descending |
            Select-Object -First 1
        if ($LatestFrontSamples) {
            $FrontSamplesPath = $LatestFrontSamples.FullName
        }
    }
}
if (-not $OutPath) {
    $OutPath = Join-Path $RuntimeRoot ".build\research\uv_plans\paintman-uv-plan-latest.json"
}

$Project = Join-Path $RuntimeRoot "tools\mesh_planner\MecchaMeshPlanner.csproj"
$ArgsList = @(
    "--mesh", $MeshPath,
    "--last-status", $LastStatusPath,
    "--out", $OutPath,
    "--texture-size", "$TextureSize",
    "--sample-step-texels", "$SampleStepTexels",
    "--max-samples", "$MaxSamples"
)
if ($FrontSamplesPath -and (Test-Path $FrontSamplesPath -PathType Leaf)) {
    $ArgsList += @("--front-samples", $FrontSamplesPath)
}
if ($CameraDirection) {
    $ArgsList += @("--camera-dir", $CameraDirection)
}

dotnet run -c Release --project $Project -- @ArgsList
if ($LASTEXITCODE -ne 0) {
    throw "mesh planner failed with exit code $LASTEXITCODE"
}
