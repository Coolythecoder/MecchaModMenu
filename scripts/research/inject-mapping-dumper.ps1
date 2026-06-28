param(
    [string]$RuntimeRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path,
    [ValidateSet("Probe", "ObjectScan", "Dump")]
    [string]$Mode = "Probe",
    [string]$ProcessName = "PenguinHotel-Win64-Shipping.exe",
    [string]$DumperDll = "",
    [string]$OutputDir = "",
    [string]$LogDir = "",
    [switch]$Force
)

$ErrorActionPreference = "Stop"

if (-not $DumperDll) {
    $DumperDll = Join-Path $RuntimeRoot ".build\research\mapping_dumper\bin\UnrealMappingsDumper.dll"
}
if (-not $OutputDir) {
    $OutputDir = Join-Path $RuntimeRoot ".build\research\mappings"
}
if (-not $LogDir) {
    $LogDir = Join-Path $RuntimeRoot ".build\research\logs"
}

if (-not (Test-Path $DumperDll -PathType Leaf)) {
    throw "Dumper DLL not found: $DumperDll. Run scripts\research\build-mapping-dumper.ps1 first."
}

$Injector = Join-Path $RuntimeRoot ".build\bin\runtime-injector.exe"
if (-not (Test-Path $Injector -PathType Leaf)) {
    throw "runtime-injector.exe not found: $Injector. Run make build first."
}

New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null
New-Item -ItemType Directory -Force -Path $LogDir | Out-Null

$DllHash = (Get-FileHash -Algorithm SHA256 $DumperDll).Hash.ToLowerInvariant()
$HistoryPath = Join-Path $RuntimeRoot ".build\research\mapping_dumper\injection-history.jsonl"
New-Item -ItemType Directory -Force -Path (Split-Path -Parent $HistoryPath) | Out-Null

if ((Test-Path $HistoryPath) -and -not $Force) {
    $PriorUse = Select-String -Path $HistoryPath -Pattern $DllHash -SimpleMatch -Quiet
    if ($PriorUse) {
        throw "This dumper build hash has already been injected. Rebuild after changes or pass -Force intentionally."
    }
}

$Stamp = Get-Date -Format "yyyyMMdd-HHmmss"
$ConfigPath = Join-Path (Split-Path -Parent $DumperDll) "UnrealMappingsDumper.config"
$LogPath = Join-Path $LogDir "mapping-dumper-$Stamp.log"
$OutputPath = Join-Path $OutputDir "Mappings.usmap"

Set-Content -Encoding ASCII -Path $ConfigPath -Value @"
mode=$($Mode.ToLowerInvariant())
log=$LogPath
output=$OutputPath
"@

& $Injector $ProcessName $DumperDll
if ($LASTEXITCODE -ne 0) {
    throw "runtime injector failed with exit code $LASTEXITCODE"
}

$Record = [ordered]@{
    timestamp = (Get-Date).ToUniversalTime().ToString("o")
    mode = $Mode.ToLowerInvariant()
    process = $ProcessName
    dll = (Resolve-Path $DumperDll).Path
    sha256 = $DllHash
    log = (Resolve-Path $LogDir).Path
    output = (Resolve-Path $OutputDir).Path
} | ConvertTo-Json -Compress
Add-Content -Encoding ASCII -Path $HistoryPath -Value $Record

Write-Host "Injected mapping dumper in $Mode mode."
Write-Host "  log: $LogPath"
Write-Host "  output: $OutputPath"
