[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$Directory,
    [string]$RepositoryRoot = ""
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$Component = "OpenRevelare.Presentation.Win32.Native"
$LibraryName = "$Component.dll"
$ManifestName = "$Component.manifest.json"
$ExpectedExports = @(
    "orwp_create",
    "orwp_resize",
    "orwp_present",
    "orwp_query_diagnostics",
    "orwp_destroy"
)
$ExpectedImports = @(
    "d3d11.dll",
    "dxgi.dll",
    "KERNEL32.dll",
    "USER32.dll"
)
$ExpectedSourcePaths = @(
    "packaging/windows/build-win32-presenter.ps1",
    "packaging/windows/smoke-win32-presenter.ps1",
    "packaging/windows/verify-win32-presenter.ps1",
    "src/OpenRevelare.Presentation.Win32.Native/OpenRevelare.Presentation.Win32.Native.vcxproj",
    "src/OpenRevelare.Presentation.Win32.Native/OpenRevelarePresentationWin32.cpp",
    "src/OpenRevelare.Presentation.Win32.Native/OpenRevelarePresentationWin32.h"
)
[Array]::Sort($ExpectedSourcePaths, [StringComparer]::Ordinal)

function Assert-Sha256([object]$Value, [string]$Field) {
    if ($Value -isnot [string] -or $Value -notmatch '^[0-9A-Fa-f]{64}$') {
        throw "$Field must be exactly 64 hexadecimal SHA-256 characters."
    }
}

function Get-BytesSha256([byte[]]$Bytes) {
    $algorithm = [System.Security.Cryptography.SHA256]::Create()
    try {
        return ([BitConverter]::ToString($algorithm.ComputeHash($Bytes))).Replace("-", "")
    }
    finally {
        $algorithm.Dispose()
    }
}

function Assert-PeRange(
    [byte[]]$Bytes,
    [long]$Offset,
    [long]$Count,
    [string]$Field) {
    if ($Offset -lt 0 -or $Count -lt 0 -or $Offset -gt $Bytes.LongLength -or
        $Count -gt ($Bytes.LongLength - $Offset)) {
        throw "Presenter PE $Field points outside the file."
    }
}

function Read-PeUInt16([byte[]]$Bytes, [long]$Offset, [string]$Field) {
    Assert-PeRange $Bytes $Offset 2 $Field
    return [uint16]([uint32]$Bytes[$Offset] -bor ([uint32]$Bytes[$Offset + 1] -shl 8))
}

function Read-PeUInt32([byte[]]$Bytes, [long]$Offset, [string]$Field) {
    Assert-PeRange $Bytes $Offset 4 $Field
    return [uint32](
        [uint32]$Bytes[$Offset] -bor
        ([uint32]$Bytes[$Offset + 1] -shl 8) -bor
        ([uint32]$Bytes[$Offset + 2] -shl 16) -bor
        ([uint32]$Bytes[$Offset + 3] -shl 24))
}

function Convert-PeRvaToOffset(
    [byte[]]$Bytes,
    [uint32]$Rva,
    [object[]]$Sections,
    [uint32]$SizeOfHeaders,
    [string]$Field) {
    if ($Rva -lt $SizeOfHeaders) {
        Assert-PeRange $Bytes $Rva 1 $Field
        return [long]$Rva
    }
    foreach ($section in $Sections) {
        [uint64]$span = [Math]::Max([uint64]$section.VirtualSize, [uint64]$section.RawSize)
        [uint64]$start = $section.VirtualAddress
        [uint64]$end = $start + $span
        if ([uint64]$Rva -ge $start -and [uint64]$Rva -lt $end) {
            [uint64]$delta = [uint64]$Rva - $start
            if ($delta -ge [uint64]$section.RawSize) {
                throw "Presenter PE $Field points into an uninitialized section tail."
            }
            [uint64]$offset = [uint64]$section.RawPointer + $delta
            if ($offset -gt [long]::MaxValue) {
                throw "Presenter PE $Field offset overflowed."
            }
            Assert-PeRange $Bytes ([long]$offset) 1 $Field
            return [long]$offset
        }
    }
    throw "Presenter PE $Field RVA 0x$($Rva.ToString('X8')) is not mapped by a section."
}

function Read-PeAsciiZ([byte[]]$Bytes, [long]$Offset, [string]$Field) {
    Assert-PeRange $Bytes $Offset 1 $Field
    $maximum = [Math]::Min(1024, $Bytes.LongLength - $Offset)
    $length = 0
    while ($length -lt $maximum -and $Bytes[$Offset + $length] -ne 0) {
        $length++
    }
    if ($length -eq 0 -or $length -eq $maximum) {
        throw "Presenter PE $Field is empty or unterminated."
    }
    return [Text.Encoding]::ASCII.GetString($Bytes, [int]$Offset, $length)
}

function Read-PresenterPeMetadata([string]$Path) {
    [byte[]]$bytes = [IO.File]::ReadAllBytes($Path)
    Assert-PeRange $bytes 0 64 "DOS header"
    if ($bytes[0] -ne 0x4D -or $bytes[1] -ne 0x5A) {
        throw "Presenter artifact is not an MZ executable."
    }
    [uint32]$peOffset = Read-PeUInt32 $bytes 0x3C "PE header offset"
    Assert-PeRange $bytes $peOffset 24 "PE/COFF header"
    if ($bytes[$peOffset] -ne 0x50 -or $bytes[$peOffset + 1] -ne 0x45 -or
        $bytes[$peOffset + 2] -ne 0 -or $bytes[$peOffset + 3] -ne 0) {
        throw "Presenter artifact has no PE signature."
    }

    [long]$coff = $peOffset + 4
    [uint16]$machine = Read-PeUInt16 $bytes $coff "COFF machine"
    [uint16]$sectionCount = Read-PeUInt16 $bytes ($coff + 2) "COFF section count"
    [uint16]$optionalSize = Read-PeUInt16 $bytes ($coff + 16) "COFF optional-header size"
    [uint16]$characteristics = Read-PeUInt16 $bytes ($coff + 18) "COFF characteristics"
    if ($machine -ne 0x8664) {
        throw "Presenter PE machine is not AMD64 (0x8664): got 0x$('{0:X4}' -f $machine)."
    }
    if ($sectionCount -eq 0 -or $sectionCount -gt 96) {
        throw "Presenter PE section count is unreasonable: $sectionCount"
    }
    if (($characteristics -band 0x2002) -ne 0x2002) {
        throw "Presenter PE is not an executable DLL image."
    }

    [long]$optional = $coff + 20
    Assert-PeRange $bytes $optional $optionalSize "optional header"
    [uint16]$magic = Read-PeUInt16 $bytes $optional "optional-header magic"
    if ($magic -ne 0x20B -or $optionalSize -lt 128) {
        throw "Presenter artifact is not a valid PE32+ image."
    }
    [uint32]$sizeOfHeaders = Read-PeUInt32 $bytes ($optional + 60) "SizeOfHeaders"
    [uint32]$directoryCount = Read-PeUInt32 $bytes ($optional + 108) "data-directory count"
    if ($directoryCount -lt 2) { throw "Presenter PE lacks export/import directories." }
    [uint32]$exportRva = Read-PeUInt32 $bytes ($optional + 112) "export-directory RVA"
    [uint32]$exportSize = Read-PeUInt32 $bytes ($optional + 116) "export-directory size"
    [uint32]$importRva = Read-PeUInt32 $bytes ($optional + 120) "import-directory RVA"
    if ($exportRva -eq 0 -or $exportSize -lt 40 -or $importRva -eq 0) {
        throw "Presenter PE export/import directory is missing."
    }

    [long]$sectionTable = $optional + $optionalSize
    Assert-PeRange $bytes $sectionTable ([long]$sectionCount * 40) "section table"
    $sections = @()
    for ($index = 0; $index -lt $sectionCount; $index++) {
        [long]$entry = $sectionTable + ([long]$index * 40)
        $sections += [pscustomobject]@{
            VirtualSize = Read-PeUInt32 $bytes ($entry + 8) "section virtual size"
            VirtualAddress = Read-PeUInt32 $bytes ($entry + 12) "section virtual address"
            RawSize = Read-PeUInt32 $bytes ($entry + 16) "section raw size"
            RawPointer = Read-PeUInt32 $bytes ($entry + 20) "section raw pointer"
        }
    }

    [long]$export = Convert-PeRvaToOffset $bytes $exportRva $sections $sizeOfHeaders "export directory"
    Assert-PeRange $bytes $export 40 "export directory"
    [uint32]$functionCount = Read-PeUInt32 $bytes ($export + 20) "export function count"
    [uint32]$nameCount = Read-PeUInt32 $bytes ($export + 24) "export name count"
    [uint32]$functionsRva = Read-PeUInt32 $bytes ($export + 28) "export function table"
    [uint32]$namesRva = Read-PeUInt32 $bytes ($export + 32) "export name table"
    [uint32]$ordinalsRva = Read-PeUInt32 $bytes ($export + 36) "export ordinal table"
    if ($functionCount -ne $nameCount -or $nameCount -eq 0 -or $nameCount -gt 256) {
        throw "Presenter PE must contain only a bounded set of named exports."
    }
    [long]$functions = Convert-PeRvaToOffset $bytes $functionsRva $sections $sizeOfHeaders "export function table"
    [long]$names = Convert-PeRvaToOffset $bytes $namesRva $sections $sizeOfHeaders "export name table"
    [long]$ordinals = Convert-PeRvaToOffset $bytes $ordinalsRva $sections $sizeOfHeaders "export ordinal table"
    Assert-PeRange $bytes $functions ([long]$functionCount * 4) "export function table"
    Assert-PeRange $bytes $names ([long]$nameCount * 4) "export name table"
    Assert-PeRange $bytes $ordinals ([long]$nameCount * 2) "export ordinal table"
    [string[]]$exports = @()
    for ($index = 0; $index -lt $nameCount; $index++) {
        [uint32]$nameRva = Read-PeUInt32 $bytes ($names + ([long]$index * 4)) "export name"
        [uint16]$ordinal = Read-PeUInt16 $bytes ($ordinals + ([long]$index * 2)) "export ordinal"
        if ($ordinal -ge $functionCount) { throw "Presenter PE export ordinal is out of range." }
        [uint32]$functionRva = Read-PeUInt32 $bytes ($functions + ([long]$ordinal * 4)) "export target"
        if ($functionRva -eq 0 -or
            ([uint64]$functionRva -ge [uint64]$exportRva -and
             [uint64]$functionRva -lt ([uint64]$exportRva + [uint64]$exportSize))) {
            throw "Presenter PE contains a null or forwarded export."
        }
        [long]$nameOffset = Convert-PeRvaToOffset $bytes $nameRva $sections $sizeOfHeaders "export name"
        $exports += Read-PeAsciiZ $bytes $nameOffset "export name"
    }

    [long]$import = Convert-PeRvaToOffset $bytes $importRva $sections $sizeOfHeaders "import directory"
    [string[]]$imports = @()
    for ($index = 0; $index -lt 256; $index++) {
        [long]$descriptor = $import + ([long]$index * 20)
        Assert-PeRange $bytes $descriptor 20 "import descriptor"
        [uint32]$originalThunk = Read-PeUInt32 $bytes $descriptor "import original thunk"
        [uint32]$timeDate = Read-PeUInt32 $bytes ($descriptor + 4) "import timestamp"
        [uint32]$forwarder = Read-PeUInt32 $bytes ($descriptor + 8) "import forwarder"
        [uint32]$nameRva = Read-PeUInt32 $bytes ($descriptor + 12) "import name"
        [uint32]$firstThunk = Read-PeUInt32 $bytes ($descriptor + 16) "import first thunk"
        if (($originalThunk -bor $timeDate -bor $forwarder -bor $nameRva -bor $firstThunk) -eq 0) {
            break
        }
        if ($nameRva -eq 0) { throw "Presenter PE import descriptor has no DLL name." }
        [long]$nameOffset = Convert-PeRvaToOffset $bytes $nameRva $sections $sizeOfHeaders "import DLL name"
        $imports += Read-PeAsciiZ $bytes $nameOffset "import DLL name"
        if ($index -eq 255) { throw "Presenter PE import descriptor list is unterminated." }
    }
    if ($imports.Count -eq 0) { throw "Presenter PE has no imports." }

    return [pscustomobject]@{ Exports = $exports; Imports = $imports }
}

function Assert-OrdinalStringSet(
    [string[]]$Actual,
    [string[]]$Expected,
    [string]$Field,
    [StringComparer]$Comparer) {
    if ($Actual.Count -ne $Expected.Count) {
        throw "$Field count mismatch: expected $($Expected.Count), got $($Actual.Count)."
    }
    [string[]]$actualSorted = @($Actual)
    [string[]]$expectedSorted = @($Expected)
    [Array]::Sort($actualSorted, $Comparer)
    [Array]::Sort($expectedSorted, $Comparer)
    for ($index = 0; $index -lt $expectedSorted.Count; $index++) {
        if (-not $Comparer.Equals($actualSorted[$index], $expectedSorted[$index])) {
            throw "$Field mismatch: expected [$($expectedSorted -join ', ')], got [$($actualSorted -join ', ')]."
        }
    }
}

$Directory = [System.IO.Path]::GetFullPath($Directory)
if (-not (Test-Path -LiteralPath $Directory -PathType Container)) {
    throw "Win32 presenter package directory does not exist: $Directory"
}
$libraryPath = Join-Path $Directory $LibraryName
$manifestPath = Join-Path $Directory $ManifestName
if (-not (Test-Path -LiteralPath $libraryPath -PathType Leaf)) {
    throw "Win32 presenter DLL is missing: $libraryPath"
}
if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
    throw "Win32 presenter manifest is missing: $manifestPath"
}

$manifest = Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
if ($manifest.schemaVersion -ne 1) { throw "Unsupported presenter manifest schemaVersion." }
if ($manifest.component -cne $Component) { throw "Presenter manifest component mismatch." }
if ($manifest.rid -cne "win-x64") { throw "Presenter manifest RID mismatch." }
if ($manifest.abi.version -ne 1) { throw "Presenter manifest ABI version mismatch." }
if ($manifest.abi.architecture -cne "x64") { throw "Presenter manifest architecture mismatch." }
if ($manifest.abi.callingConvention -cne "cdecl") { throw "Presenter manifest calling convention mismatch." }

$exports = @($manifest.abi.exports)
if ($exports.Count -ne $ExpectedExports.Count) { throw "Presenter manifest export count mismatch." }
for ($index = 0; $index -lt $ExpectedExports.Count; $index++) {
    if (([string]$exports[$index]) -cne $ExpectedExports[$index]) {
        throw "Presenter manifest export mismatch at index $index."
    }
}

if ($manifest.source.identityAlgorithm -cne "sha256(path-lf-sha256-lf); paths sorted ordinal") {
    throw "Presenter manifest source-tree identity algorithm mismatch."
}
Assert-Sha256 $manifest.source.treeSha256 "source.treeSha256"
$sourceFiles = @($manifest.source.files)
if ($sourceFiles.Count -ne $ExpectedSourcePaths.Count) {
    throw "Presenter manifest source-tree file list is not the exact required set."
}
$seenSourcePaths = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
$sourceIdentity = [Text.StringBuilder]::new()
$previousSourcePath = $null
$sourceIndex = 0
foreach ($sourceFile in $sourceFiles) {
    if ($sourceFile.path -isnot [string] -or [string]::IsNullOrWhiteSpace($sourceFile.path) -or
        [System.IO.Path]::IsPathRooted($sourceFile.path) -or $sourceFile.path.Contains('\')) {
        throw "Presenter manifest contains a non-canonical source path."
    }
    if (-not $seenSourcePaths.Add([string]$sourceFile.path)) {
        throw "Presenter manifest contains a duplicate source path: $($sourceFile.path)"
    }
    Assert-Sha256 $sourceFile.sha256 "source.files[$($sourceFile.path)].sha256"
    if ($null -ne $previousSourcePath -and
        [StringComparer]::Ordinal.Compare([string]$previousSourcePath, [string]$sourceFile.path) -ge 0) {
        throw "Presenter manifest source paths are not sorted ordinal."
    }
    if (([string]$sourceFile.path) -cne $ExpectedSourcePaths[$sourceIndex]) {
        throw "Presenter manifest source path mismatch."
    }
    [void]$sourceIdentity.Append([string]$sourceFile.path).Append("`n").Append(
        ([string]$sourceFile.sha256).ToUpperInvariant()).Append("`n")
    $previousSourcePath = [string]$sourceFile.path
    $sourceIndex++
}

if (-not [string]::IsNullOrWhiteSpace($RepositoryRoot)) {
    $RepositoryRoot = [IO.Path]::GetFullPath($RepositoryRoot)
    foreach ($sourceFile in $sourceFiles) {
        $relativeNativePath = ([string]$sourceFile.path).Replace(
            '/', [IO.Path]::DirectorySeparatorChar)
        $sourcePath = [IO.Path]::GetFullPath((Join-Path $RepositoryRoot $relativeNativePath))
        $rootPrefix = $RepositoryRoot.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
        if (-not $sourcePath.StartsWith($rootPrefix, [StringComparison]::OrdinalIgnoreCase) -or
            -not (Test-Path -LiteralPath $sourcePath -PathType Leaf)) {
            throw "Presenter source file is missing or escaped the repository root: $($sourceFile.path)"
        }
        $repositoryHash = (Get-FileHash -LiteralPath $sourcePath -Algorithm SHA256).Hash
        if ($repositoryHash -cne ([string]$sourceFile.sha256).ToUpperInvariant()) {
            throw "Presenter manifest source hash does not match repository file: $($sourceFile.path)"
        }
    }
}
$identityBytes = [Text.UTF8Encoding]::new($false).GetBytes($sourceIdentity.ToString())
$computedSourceIdentity = Get-BytesSha256 $identityBytes
if ($computedSourceIdentity -cne ([string]$manifest.source.treeSha256).ToUpperInvariant()) {
    throw "Presenter manifest source-tree SHA-256 does not match its file list."
}

if ($manifest.build.reproducible -ne $true -or $manifest.build.verificationBuilds -lt 2 -or
    $manifest.build.sourceSnapshotVerified -ne $true) {
    throw "Presenter manifest does not attest a stable-source two-build reproducibility check."
}

$artifacts = @($manifest.artifacts)
if ($artifacts.Count -ne 1) { throw "Presenter manifest must describe exactly one artifact." }
$artifact = $artifacts[0]
if ($artifact.file -cne $LibraryName) { throw "Presenter manifest artifact filename mismatch." }
Assert-Sha256 $artifact.sha256 "artifacts[0].sha256"
$libraryInfo = Get-Item -LiteralPath $libraryPath
if (([long]$artifact.size) -ne ([long]$libraryInfo.Length)) {
    throw "Presenter DLL size does not match the manifest."
}
$actualHash = (Get-FileHash -LiteralPath $libraryPath -Algorithm SHA256).Hash.ToUpperInvariant()
if ($actualHash -cne ([string]$artifact.sha256).ToUpperInvariant()) {
    throw "Presenter DLL SHA-256 does not match the manifest: expected $($artifact.sha256), got $actualHash"
}

$pe = Read-PresenterPeMetadata $libraryPath
Assert-OrdinalStringSet @($pe.Exports) $ExpectedExports "Presenter PE exports" ([StringComparer]::Ordinal)
Assert-OrdinalStringSet @($pe.Imports) $ExpectedImports "Presenter PE imports" ([StringComparer]::OrdinalIgnoreCase)

Write-Host "Verified $LibraryName (PE32+ AMD64, ABI 1, static CRT import set, SHA-256 $actualHash)"
