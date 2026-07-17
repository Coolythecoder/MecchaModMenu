param(
    [string]$Version = "",
    [string]$OutDir = "",
    [string]$ExePath = "",
    [string]$RuntimeRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
)

$ErrorActionPreference = "Stop"

function Resolve-ProjectVersion {
    param(
        [string]$Requested,
        [string]$Root
    )
    if (-not [string]::IsNullOrWhiteSpace($Requested)) {
        return $Requested
    }
    if (Get-Command git -ErrorAction SilentlyContinue) {
        $exact = & git -C $Root describe --tags --exact-match 2>$null
        if ($LASTEXITCODE -eq 0 -and -not [string]::IsNullOrWhiteSpace($exact)) {
            return $exact.Trim()
        }
        $described = & git -C $Root describe --tags --dirty --always 2>$null
        if ($LASTEXITCODE -eq 0 -and -not [string]::IsNullOrWhiteSpace($described)) {
            return $described.Trim()
        }
    }
    return "unversioned"
}

$Version = Resolve-ProjectVersion -Requested $Version -Root $RuntimeRoot
Write-Host "Package version: $Version"

if (-not $OutDir) { $OutDir = Join-Path $RuntimeRoot ".build\package" }
$ArtifactName = "meccha-mod-menu-$Version"
if (-not $ExePath) { $ExePath = Join-Path $RuntimeRoot ".build\bin\meccha-mod-menu.exe" }
if (-not (Test-Path $ExePath -PathType Leaf)) { throw "Executable not found: $ExePath. Run scripts/build.ps1 first." }

New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
$DebugArtifacts = @(
    Get-ChildItem -Path $OutDir -File -Recurse |
        Where-Object { $_.Extension -in @(".pdb", ".dbg", ".ilk") }
)
if ($DebugArtifacts.Count -gt 0) {
    throw "Release output directory contains debug artifacts: $($DebugArtifacts.FullName -join ', ')"
}
$ArtifactPath = Join-Path $OutDir "$ArtifactName.exe"
if (Test-Path $ArtifactPath) { Remove-Item -Force $ArtifactPath }
Copy-Item -Force -Path $ExePath -Destination $ArtifactPath
if ((Get-Item $ArtifactPath).Length -le 0) { throw "Release artifact is empty: $ArtifactPath" }
Write-Host "Wrote $ArtifactPath"
