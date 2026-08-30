[CmdletBinding()]
param(
    [string]$RepositoryRoot = ""
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"

$Component = "OpenRevelare.Presentation.Win32.Native"
$LibraryName = "$Component.dll"
$ManifestName = "$Component.manifest.json"
$ManifestSchemaVersion = 1
$AbiVersion = 1
$WindowsSdkVersion = "10.0.26100.0"
$RequiredExports = @(
    "orwp_create",
    "orwp_resize",
    "orwp_present",
    "orwp_query_diagnostics",
    "orwp_destroy"
)

if ([string]::IsNullOrWhiteSpace($RepositoryRoot)) {
    $RepositoryRoot = Join-Path $PSScriptRoot "..\.."
}
$RepositoryRoot = [System.IO.Path]::GetFullPath($RepositoryRoot)

$projectRelativePath = "src/OpenRevelare.Presentation.Win32.Native/OpenRevelare.Presentation.Win32.Native.vcxproj"
$project = Join-Path $RepositoryRoot ($projectRelativePath.Replace('/', [System.IO.Path]::DirectorySeparatorChar))
if (-not (Test-Path -LiteralPath $project -PathType Leaf)) {
    throw "Repository root does not contain the Win32 presenter project: $RepositoryRoot"
}
$abiHeader = Join-Path $RepositoryRoot `
    "src\OpenRevelare.Presentation.Win32.Native\OpenRevelarePresentationWin32.h"
$abiHeaderText = Get-Content -LiteralPath $abiHeader -Raw -Encoding UTF8
if ($abiHeaderText -notmatch "ORWP_ABI_VERSION\s*=\s*$AbiVersion\b") {
    throw "Build-script ABI version $AbiVersion does not match OpenRevelarePresentationWin32.h."
}

function Get-MsBuildPath {
    $command = Get-Command msbuild.exe -ErrorAction SilentlyContinue
    if ($null -ne $command) {
        $candidate = [System.IO.Path]::GetFullPath($command.Source)
        if ([Diagnostics.FileVersionInfo]::GetVersionInfo($candidate).FileMajorPart -eq 17) {
            return $candidate
        }
    }

    $programFilesX86 = [Environment]::GetFolderPath([Environment+SpecialFolder]::ProgramFilesX86)
    $vswhere = Join-Path $programFilesX86 "Microsoft Visual Studio\Installer\vswhere.exe"
    if (-not (Test-Path -LiteralPath $vswhere -PathType Leaf)) {
        throw "MSBuild was not found in PATH and vswhere.exe is unavailable. Install Visual Studio 2022 with Desktop development with C++."
    }

    $found = & $vswhere -latest -version "[17.0,18.0)" -products * `
        -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 `
        -find "MSBuild\**\Bin\MSBuild.exe" | Select-Object -First 1
    if ([string]::IsNullOrWhiteSpace($found)) {
        # Some VS installations do not expose workload component IDs consistently. The project
        # build remains the authoritative toolchain check.
        $found = & $vswhere -latest -version "[17.0,18.0)" -products * `
            -find "MSBuild\**\Bin\MSBuild.exe" | Select-Object -First 1
    }
    if ([string]::IsNullOrWhiteSpace($found)) {
        throw "Visual Studio 2022 MSBuild was not found."
    }
    return [System.IO.Path]::GetFullPath([string]$found)
}

function Get-UpperSha256([string]$Path) {
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToUpperInvariant()
}

function Get-BytesSha256([byte[]]$Bytes) {
    $algorithm = [System.Security.Cryptography.SHA256]::Create()
    try {
        $digest = $algorithm.ComputeHash($Bytes)
        return ([BitConverter]::ToString($digest)).Replace("-", "")
    }
    finally {
        $algorithm.Dispose()
    }
}

$msbuild = Get-MsBuildPath
$msbuildVersionInfo = [Diagnostics.FileVersionInfo]::GetVersionInfo($msbuild)
if ($msbuildVersionInfo.FileMajorPart -ne 17) {
    throw "The selected MSBuild is not from Visual Studio 2022: $msbuild"
}
$msbuildVersion = $msbuildVersionInfo.ProductVersion
if ([string]::IsNullOrWhiteSpace($msbuildVersion)) {
    throw "Could not determine the selected MSBuild product version: $msbuild"
}
$windowsSdkProgramFiles = [Environment]::GetFolderPath(
    [Environment+SpecialFolder]::ProgramFilesX86)
$windowsSdkInclude = Join-Path $windowsSdkProgramFiles "Windows Kits\10\Include\$WindowsSdkVersion"
if (-not (Test-Path -LiteralPath $windowsSdkInclude -PathType Container)) {
    throw "The pinned Windows SDK $WindowsSdkVersion is not installed: $windowsSdkInclude"
}

[string[]]$sourceRelativePaths = @(
    "packaging/windows/build-win32-presenter.ps1",
    "packaging/windows/smoke-win32-presenter.ps1",
    "packaging/windows/verify-win32-presenter.ps1",
    "src/OpenRevelare.Presentation.Win32.Native/OpenRevelare.Presentation.Win32.Native.vcxproj",
    "src/OpenRevelare.Presentation.Win32.Native/OpenRevelarePresentationWin32.cpp",
    "src/OpenRevelare.Presentation.Win32.Native/OpenRevelarePresentationWin32.h"
)
[Array]::Sort($sourceRelativePaths, [StringComparer]::Ordinal)
$sourceHashesBeforeBuild = @{}
foreach ($relativePath in $sourceRelativePaths) {
    $nativePath = $relativePath.Replace('/', [System.IO.Path]::DirectorySeparatorChar)
    $fullPath = Join-Path $RepositoryRoot $nativePath
    if (-not (Test-Path -LiteralPath $fullPath -PathType Leaf)) {
        throw "Native presenter source-tree input is missing: $relativePath"
    }
    $sourceHashesBeforeBuild[$relativePath] = Get-UpperSha256 $fullPath
}

$temporaryBase = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath()).TrimEnd('\', '/')
$workName = "openrevelare-win32-presenter-$PID-$([Guid]::NewGuid().ToString('N'))"
$work = [System.IO.Path]::GetFullPath((Join-Path $temporaryBase $workName))
$workParent = [System.IO.DirectoryInfo]::new($work).Parent.FullName.TrimEnd('\', '/')
if (-not [string]::Equals($workParent, $temporaryBase, [StringComparison]::OrdinalIgnoreCase) -or
    -not ([System.IO.Path]::GetFileName($work)).StartsWith(
        "openrevelare-win32-presenter-", [StringComparison]::Ordinal)) {
    throw "Refusing unsafe temporary directory: $work"
}
New-Item -ItemType Directory -Path $work | Out-Null
$toolEnvironmentNames = @("CL", "_CL_", "LINK", "_LINK_")
$originalToolEnvironment = @{}
try {
    foreach ($name in $toolEnvironmentNames) {
        $originalToolEnvironment[$name] = [Environment]::GetEnvironmentVariable(
            $name, [EnvironmentVariableTarget]::Process)
        [Environment]::SetEnvironmentVariable(
            $name, $null, [EnvironmentVariableTarget]::Process)
    }

    $emptyUserRoot = Join-Path $work "empty-user-root"
    New-Item -ItemType Directory -Path $emptyUserRoot | Out-Null
    $emptyUserRootWithSeparator = $emptyUserRoot + [System.IO.Path]::DirectorySeparatorChar
    $reproducibleOverrides = Join-Path $work "openrevelare-win32-presenter-reproducible.targets"
    $overrideXml = @'
<Project xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
  <PropertyGroup>
    <Deterministic>true</Deterministic>
    <LinkIncremental>false</LinkIncremental>
  </PropertyGroup>
  <ItemDefinitionGroup>
    <ClCompile>
      <DebugInformationFormat>None</DebugInformationFormat>
      <RuntimeLibrary>MultiThreaded</RuntimeLibrary>
      <AdditionalOptions>/Brepro %(AdditionalOptions)</AdditionalOptions>
    </ClCompile>
    <Link>
      <GenerateDebugInformation>false</GenerateDebugInformation>
      <AdditionalOptions>/Brepro /DEBUG:NONE /INCREMENTAL:NO %(AdditionalOptions)</AdditionalOptions>
    </Link>
  </ItemDefinitionGroup>
</Project>
'@
    [System.IO.File]::WriteAllText(
        $reproducibleOverrides,
        $overrideXml + [Environment]::NewLine,
        [System.Text.UTF8Encoding]::new($false))

    function Invoke-PresenterBuild([string]$Name) {
        $buildRoot = Join-Path $work $Name
        $output = Join-Path $buildRoot "out"
        $intermediate = Join-Path $buildRoot "obj"
        New-Item -ItemType Directory -Path $output -Force | Out-Null
        New-Item -ItemType Directory -Path $intermediate -Force | Out-Null
        $outputWithSeparator = $output + [System.IO.Path]::DirectorySeparatorChar
        $intermediateWithSeparator = $intermediate + [System.IO.Path]::DirectorySeparatorChar

        Write-Host "==> Building Win32 presenter $Name (Release|x64, /m:1)"
        $arguments = @(
            $project,
            "/nologo",
            "/m:1",
            "/t:Rebuild",
            "/p:Configuration=Release",
            "/p:Platform=x64",
            "/p:PlatformToolset=v143",
            "/p:PreferredToolArchitecture=x64",
            "/p:WindowsTargetPlatformVersion=$WindowsSdkVersion",
            "/p:Deterministic=true",
            # Each invocation is an isolated /t:Rebuild tree, so MSB8029's incremental-build
            # warning for TEMP-based IntDir/OutDir does not apply.
            "/p:IgnoreWarnIntDirInTempDetected=true",
            "/p:UserRootDir=$emptyUserRootWithSeparator",
            "/p:OutDir=$outputWithSeparator",
            "/p:IntDir=$intermediateWithSeparator",
            "/p:ForceImportBeforeCppTargets=$reproducibleOverrides",
            "/verbosity:minimal"
        )
        & $msbuild @arguments | Out-Host
        if ($LASTEXITCODE -ne 0) {
            throw "MSBuild $Name failed with exit code $LASTEXITCODE"
        }

        $library = Join-Path $output $LibraryName
        if (-not (Test-Path -LiteralPath $library -PathType Leaf)) {
            throw "MSBuild $Name succeeded but did not produce $library"
        }
        $unexpectedPdb = Get-ChildItem -LiteralPath $buildRoot -Filter "*.pdb" -File -Recurse |
            Select-Object -First 1
        if ($null -ne $unexpectedPdb) {
            throw "Reproducible Release build unexpectedly produced a PDB: $($unexpectedPdb.FullName)"
        }
        return $library
    }

    $firstLibrary = Invoke-PresenterBuild "repro-1"
    $secondLibrary = Invoke-PresenterBuild "repro-2"
    $firstHash = Get-UpperSha256 $firstLibrary
    $secondHash = Get-UpperSha256 $secondLibrary
    if ($firstHash -ne $secondHash) {
        throw "Win32 presenter reproducibility check failed: first=$firstHash second=$secondHash"
    }

    $destination = Join-Path $RepositoryRoot "native\win-x64"
    New-Item -ItemType Directory -Path $destination -Force | Out-Null
    $outputLibrary = Join-Path $destination $LibraryName
    Copy-Item -LiteralPath $firstLibrary -Destination $outputLibrary -Force
    $artifactHash = Get-UpperSha256 $outputLibrary
    if ($artifactHash -ne $firstHash) {
        throw "Assembled presenter hash changed during copy: built=$firstHash assembled=$artifactHash"
    }

    $sourceFiles = @()
    $identityText = [Text.StringBuilder]::new()
    foreach ($relativePath in $sourceRelativePaths) {
        $nativePath = $relativePath.Replace('/', [System.IO.Path]::DirectorySeparatorChar)
        $fullPath = Join-Path $RepositoryRoot $nativePath
        if (-not (Test-Path -LiteralPath $fullPath -PathType Leaf)) {
            throw "Native presenter source-tree input is missing: $relativePath"
        }
        $hash = Get-UpperSha256 $fullPath
        if ($hash -cne [string]$sourceHashesBeforeBuild[$relativePath]) {
            throw "Native presenter source changed during the two verification builds: $relativePath"
        }
        $sourceFiles += [ordered]@{
            path = $relativePath
            sha256 = $hash
        }
        [void]$identityText.Append($relativePath).Append("`n").Append($hash).Append("`n")
    }
    $identityBytes = [System.Text.UTF8Encoding]::new($false).GetBytes($identityText.ToString())
    $sourceTreeHash = Get-BytesSha256 $identityBytes

    $artifactSize = [long](Get-Item -LiteralPath $outputLibrary).Length
    $manifest = [ordered]@{
        schemaVersion = $ManifestSchemaVersion
        component = $Component
        rid = "win-x64"
        abi = [ordered]@{
            version = $AbiVersion
            architecture = "x64"
            callingConvention = "cdecl"
            exports = $RequiredExports
        }
        source = [ordered]@{
            identityAlgorithm = "sha256(path-lf-sha256-lf); paths sorted ordinal"
            treeSha256 = $sourceTreeHash
            files = $sourceFiles
        }
        build = [ordered]@{
            system = "MSBuild"
            project = $projectRelativePath
            configuration = "Release"
            platform = "x64"
            platformToolset = "v143"
            preferredToolArchitecture = "x64"
            windowsSdkVersion = $WindowsSdkVersion
            msbuildProductVersion = $msbuildVersion
            reproducible = $true
            verificationBuilds = 2
            sourceSnapshotVerified = $true
            isolatedInputs = @("CL", "_CL_", "LINK", "_LINK_", "UserRootDir")
            suppressedWarnings = @("MSB8029: isolated /t:Rebuild outputs intentionally use TEMP")
            compilerOptions = @("/Brepro", "/MT", "/permissive-", "debug information disabled")
            linkerOptions = @("/Brepro", "/DEBUG:NONE", "/INCREMENTAL:NO")
        }
        artifacts = @(
            [ordered]@{
                file = $LibraryName
                size = $artifactSize
                sha256 = $artifactHash
            }
        )
    }
    $manifestPath = Join-Path $destination $ManifestName
    $json = $manifest | ConvertTo-Json -Depth 10
    [System.IO.File]::WriteAllText(
        $manifestPath,
        $json + [Environment]::NewLine,
        [System.Text.UTF8Encoding]::new($false))

    & (Join-Path $PSScriptRoot "verify-win32-presenter.ps1") `
        -Directory $destination -RepositoryRoot $RepositoryRoot
    & (Join-Path $PSScriptRoot "smoke-win32-presenter.ps1") -Directory $destination

    Write-Host "==> Win32 presenter assembled in $destination"
    Write-Host "    DLL SHA-256       $artifactHash"
    Write-Host "    source tree SHA-256 $sourceTreeHash"
    Write-Host "    manifest          $manifestPath"
}
finally {
    foreach ($name in $toolEnvironmentNames) {
        if ($originalToolEnvironment.ContainsKey($name)) {
            [Environment]::SetEnvironmentVariable(
                $name,
                $originalToolEnvironment[$name],
                [EnvironmentVariableTarget]::Process)
        }
    }
    if (Test-Path -LiteralPath $work) {
        $resolvedWork = [System.IO.Path]::GetFullPath($work)
        $resolvedParent = [System.IO.DirectoryInfo]::new($resolvedWork).Parent.FullName.TrimEnd('\', '/')
        if ([string]::Equals($resolvedParent, $temporaryBase, [StringComparison]::OrdinalIgnoreCase) -and
            ([System.IO.Path]::GetFileName($resolvedWork)).StartsWith(
                "openrevelare-win32-presenter-", [StringComparison]::Ordinal)) {
            Remove-Item -LiteralPath $resolvedWork -Recurse -Force
        }
        else {
            Write-Warning "Refusing to clean unexpected temporary directory: $resolvedWork"
        }
    }
}
