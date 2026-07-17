param(
    [string]$RuntimeRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path,
    [string]$OutDir = (Join-Path $env:LOCALAPPDATA "MecchaResearch\replication-runner")
)

$ErrorActionPreference = "Stop"

function Assert-SafeOutputDirectory([string]$Path) {
    $full = [System.IO.Path]::GetFullPath($Path).TrimEnd("\", "/")
    $root = [System.IO.Path]::GetPathRoot($full).TrimEnd("\", "/")
    if ([string]::IsNullOrWhiteSpace($full) -or $full -eq $root) {
        throw "Refusing to use a filesystem root as research output: $Path"
    }
    return $full
}

function Invoke-VsCommand([string]$CommandLine) {
    $vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
    if (-not (Test-Path $vswhere)) {
        throw "Visual Studio locator not found: $vswhere"
    }
    $install = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
    if ([string]::IsNullOrWhiteSpace($install)) {
        throw "Visual Studio x64 C++ tools are required for the native research bridge."
    }
    $devCmd = Join-Path $install "Common7\Tools\VsDevCmd.bat"
    if (-not (Test-Path $devCmd)) {
        throw "Visual Studio developer command script not found: $devCmd"
    }
    cmd.exe /d /c "call `"$devCmd`" -arch=x64 -host_arch=x64 >nul && $CommandLine"
    if ($LASTEXITCODE -ne 0) {
        throw "Visual Studio command failed with exit code $LASTEXITCODE"
    }
}

$RuntimeRoot = (Resolve-Path $RuntimeRoot).Path
$OutDir = Assert-SafeOutputDirectory $OutDir
$NativeDir = Join-Path $OutDir "native"
$RunnerDir = Join-Path $OutDir "runner"
$BridgeSource = Join-Path $RuntimeRoot "src\native\bridge\bridge.cpp"
$InjectorSource = Join-Path $RuntimeRoot "src\native\injector\injector.cpp"
$TransformValidationTestSource = Join-Path $RuntimeRoot "src\native\tests\transform_validation_test.cpp"
$WebHostProject = Join-Path $RuntimeRoot "src\csharp\MecchaCamouflage.WebHost\MecchaCamouflage.WebHost.csproj"
$MeshProfilesDir = Join-Path $RuntimeRoot "resources\mesh-profiles"

foreach ($path in @($BridgeSource, $InjectorSource, $TransformValidationTestSource, $WebHostProject, $MeshProfilesDir)) {
    if (-not (Test-Path $path)) {
        throw "Required research build input is missing: $path"
    }
}

Remove-Item -LiteralPath $OutDir -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $NativeDir | Out-Null

$BridgeOutput = Join-Path $NativeDir "runtime-bridge.dll"
$InjectorOutput = Join-Path $NativeDir "runtime-injector.exe"
$TransformValidationTestOutput = Join-Path $OutDir "transform-validation-test.exe"
Invoke-VsCommand "cl.exe /nologo /std:c++17 /EHsc /O2 `"$TransformValidationTestSource`" /Fo:`"$(Join-Path $OutDir 'transform-validation-test.obj')`" /Fe:`"$TransformValidationTestOutput`""
& $TransformValidationTestOutput
if ($LASTEXITCODE -ne 0) {
    throw "Native transform validation test failed with exit code $LASTEXITCODE"
}
Invoke-VsCommand "cl.exe /nologo /std:c++17 /EHsc /O2 /LD `"$BridgeSource`" /Fo:`"$(Join-Path $OutDir 'bridge.obj')`" /Fe:`"$BridgeOutput`" Ws2_32.lib User32.lib /link /Brepro"
Invoke-VsCommand "cl.exe /nologo /EHsc /O2 `"$InjectorSource`" /Fo:`"$(Join-Path $OutDir 'injector.obj')`" /Fe:`"$InjectorOutput`" /link /Brepro"

& dotnet publish $WebHostProject -c Release -r win-x64 --self-contained true -o $RunnerDir `
    "/p:MecchaAppVersion=research-issue87" `
    "/p:MecchaResearchBuild=true" `
    "/p:MecchaNativeRuntimeDir=$NativeDir" `
    "/p:MecchaMeshProfilesDir=$MeshProfilesDir" `
    "/p:PublishSingleFile=true" `
    "/p:IncludeAllContentForSelfExtract=true" `
    "/p:IncludeNativeLibrariesForSelfExtract=true"
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE"
}

$RunnerPath = Join-Path $RunnerDir "meccha-mod-menu.exe"
if (-not (Test-Path $RunnerPath -PathType Leaf)) {
    throw "Research runner was not produced: $RunnerPath"
}

Get-Item $RunnerPath | Select-Object FullName, Length
Get-FileHash -Algorithm SHA256 $RunnerPath
