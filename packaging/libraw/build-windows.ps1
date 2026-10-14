[CmdletBinding()]
param()
$ErrorActionPreference = "Stop"
$root = (Resolve-Path (Join-Path $PSScriptRoot "../..")).Path
$cmakeCommand = Get-Command cmake -ErrorAction SilentlyContinue
if ($null -eq $cmakeCommand) {
    $vswhere = Join-Path ${env:ProgramFiles(x86)} "Microsoft Visual Studio/Installer/vswhere.exe"
    $cmakePath = & $vswhere -latest -products * -find "Common7/IDE/CommonExtensions/Microsoft/CMake/CMake/bin/cmake.exe" | Select-Object -First 1
    if (-not $cmakePath) { throw "Install CMake 3.21+ and the Visual Studio C++ workload." }
} else { $cmakePath = $cmakeCommand.Source }
$work = Join-Path $root "scratch/libraw-build-win-x64"
$destination = Join-Path $root "native/win-x64"
& $cmakePath -S $PSScriptRoot -B $work -A x64 "-DCMAKE_INSTALL_PREFIX=$destination"
if ($LASTEXITCODE -ne 0) { throw "LibRaw configure failed" }
& $cmakePath --build $work --config Release --target OpenRevelare.Raw.Native --parallel
if ($LASTEXITCODE -ne 0) { throw "LibRaw build failed" }
& $cmakePath --install $work --config Release --component Raw
if ($LASTEXITCODE -ne 0) { throw "LibRaw install failed" }
