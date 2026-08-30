[CmdletBinding()]
param(
    [string]$RepositoryRoot = ""
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"

$LcmsVersion = "2.19.1"
$SourceUrl = "https://github.com/mm2/Little-CMS/releases/download/lcms2.19.1/lcms2-2.19.1.tar.gz"
$SourceSha256 = "BFC54F7BAB59FBC921012014A8032E4CBA4ABD46DB47D46B76416A8C0B2815C8"

if ([string]::IsNullOrWhiteSpace($RepositoryRoot)) {
    $RepositoryRoot = Join-Path $PSScriptRoot "..\.."
}
$RepositoryRoot = [System.IO.Path]::GetFullPath($RepositoryRoot)
if (-not (Test-Path -LiteralPath (Join-Path $RepositoryRoot "THIRD_PARTY_NOTICES.txt") -PathType Leaf)) {
    throw "Repository root does not look like OpenRevelare: $RepositoryRoot"
}

$destination = Join-Path $RepositoryRoot "native\win-x64"
New-Item -ItemType Directory -Path $destination -Force | Out-Null

$temporaryBase = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath())
$workName = "openrevelare-lcms-$PID-$([Guid]::NewGuid().ToString('N'))"
$work = [System.IO.Path]::GetFullPath((Join-Path $temporaryBase $workName))
if (-not $work.StartsWith($temporaryBase, [StringComparison]::OrdinalIgnoreCase) -or
    -not ([System.IO.Path]::GetFileName($work)).StartsWith("openrevelare-lcms-", [StringComparison]::Ordinal)) {
    throw "Refusing unsafe temporary directory: $work"
}
New-Item -ItemType Directory -Path $work | Out-Null

try {
    $archive = Join-Path $work "lcms2-$LcmsVersion.tar.gz"
    Write-Host "==> Downloading Little CMS $LcmsVersion"
    Invoke-WebRequest -Uri $SourceUrl -OutFile $archive

    $actualSourceHash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToUpperInvariant()
    if ($actualSourceHash -ne $SourceSha256) {
        throw "Little CMS source SHA-256 mismatch: expected $SourceSha256, got $actualSourceHash"
    }

    & tar -xzf $archive -C $work
    if ($LASTEXITCODE -ne 0) { throw "tar extraction failed with exit code $LASTEXITCODE" }

    $sourceRoot = Join-Path $work "lcms2-$LcmsVersion"
    $project = Join-Path $sourceRoot "Projects\VC2022\lcms2_DLL\lcms2_DLL.vcxproj"
    if (-not (Test-Path -LiteralPath $project -PathType Leaf)) {
        throw "Official VC2022 lcms2_DLL project is missing: $project"
    }

    # The upstream project enables LTCG and emits ordinary timestamp/build-path metadata, so two
    # clean builds of the same release otherwise produce different DLL bytes. Import these build
    # settings after the upstream project without modifying its source. /Brepro removes volatile
    # compiler/linker metadata and disabling the release PDB keeps the random temporary path out
    # of the image. /m:1 below also fixes build ordering.
    $reproducibleOverrides = Join-Path $work "openrevelare-lcms-reproducible.targets"
    $overrideXml = @'
<Project xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
  <ItemDefinitionGroup>
    <ClCompile>
      <AdditionalOptions>/Brepro %(AdditionalOptions)</AdditionalOptions>
    </ClCompile>
    <Link>
      <AdditionalOptions>/Brepro %(AdditionalOptions)</AdditionalOptions>
      <GenerateDebugInformation>false</GenerateDebugInformation>
    </Link>
  </ItemDefinitionGroup>
  <PropertyGroup>
    <LinkIncremental>false</LinkIncremental>
  </PropertyGroup>
</Project>
'@
    [System.IO.File]::WriteAllText(
        $reproducibleOverrides,
        $overrideXml + [Environment]::NewLine,
        [System.Text.UTF8Encoding]::new($false))

    $msbuildCommand = Get-Command msbuild.exe -ErrorAction SilentlyContinue
    if ($null -ne $msbuildCommand) {
        $msbuild = $msbuildCommand.Source
    }
    else {
        $vswhere = Join-Path ${env:ProgramFiles(x86)} "Microsoft Visual Studio\Installer\vswhere.exe"
        if (-not (Test-Path -LiteralPath $vswhere -PathType Leaf)) {
            throw "MSBuild was not found in PATH and vswhere.exe is unavailable"
        }
        # Some complete Community installations do not expose the historical
        # Microsoft.Component.MSBuild component ID through vswhere even though the supported
        # MSBuild and C++ workload are installed. Locate MSBuild from the selected VS instance;
        # the project build below remains the authoritative C++-toolchain check.
        $msbuild = & $vswhere -latest -products * `
            -find "MSBuild\**\Bin\MSBuild.exe" | Select-Object -First 1
        if ([string]::IsNullOrWhiteSpace($msbuild)) {
            throw "Visual Studio MSBuild with the C++ toolchain was not found"
        }
    }

    Write-Host "==> Building official VC2022 lcms2_DLL project (Release|x64)"
    & $msbuild $project /nologo /m:1 /t:Build /p:Configuration=Release /p:Platform=x64 `
        /p:Deterministic=true "/p:ForceImportBeforeCppTargets=$reproducibleOverrides" `
        /verbosity:minimal
    if ($LASTEXITCODE -ne 0) { throw "MSBuild failed with exit code $LASTEXITCODE" }

    $builtLibrary = Join-Path $sourceRoot "bin\lcms2.dll"
    if (-not (Test-Path -LiteralPath $builtLibrary -PathType Leaf)) {
        throw "MSBuild succeeded but did not produce $builtLibrary"
    }

    $outputLibrary = Join-Path $destination "lcms2.dll"
    Copy-Item -LiteralPath $builtLibrary -Destination $outputLibrary -Force
    $libraryHash = (Get-FileHash -LiteralPath $outputLibrary -Algorithm SHA256).Hash.ToUpperInvariant()

    $manifest = [ordered]@{
        schemaVersion = 1
        component = "Little CMS"
        version = $LcmsVersion
        rid = "win-x64"
        source = [ordered]@{
            url = $SourceUrl
            sha256 = $SourceSha256
        }
        build = [ordered]@{
            system = "MSBuild"
            project = "Projects/VC2022/lcms2_DLL/lcms2_DLL.vcxproj"
            configuration = "Release"
            platform = "x64"
            platformToolset = "v143"
            reproducible = $true
            compilerOptions = @("/Brepro")
            linkerOptions = @("/Brepro", "/DEBUG:NONE", "/INCREMENTAL:NO")
        }
        artifacts = @(
            [ordered]@{
                file = "lcms2.dll"
                sha256 = $libraryHash
            }
        )
    }
    $manifestPath = Join-Path $destination "lcms2.manifest.json"
    $json = $manifest | ConvertTo-Json -Depth 8
    [System.IO.File]::WriteAllText(
        $manifestPath,
        $json + [Environment]::NewLine,
        [System.Text.UTF8Encoding]::new($false))

    Write-Host "==> Little CMS assembled at $outputLibrary"
    Write-Host "    SHA-256 $libraryHash"
    Write-Host "    Manifest $manifestPath"
}
finally {
    if (Test-Path -LiteralPath $work) {
        $resolvedWork = [System.IO.Path]::GetFullPath($work)
        if ($resolvedWork.StartsWith($temporaryBase, [StringComparison]::OrdinalIgnoreCase) -and
            ([System.IO.Path]::GetFileName($resolvedWork)).StartsWith("openrevelare-lcms-", [StringComparison]::Ordinal)) {
            Remove-Item -LiteralPath $resolvedWork -Recurse -Force
        }
        else {
            Write-Warning "Refusing to clean unexpected temporary directory: $resolvedWork"
        }
    }
}
