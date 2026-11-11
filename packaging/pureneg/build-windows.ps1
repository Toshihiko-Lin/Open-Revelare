# Build the Windows x64 Node-API accelerator from a checkout of the pinned PURE Neg fork.
# Build the GUI with -p:PureNegNativeDirectory=<OutputDir> to include it in a local distribution.
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $ForkSource,
    [string] $OutputDir = (Join-Path $PSScriptRoot '../../artifacts/pureneg-native-component')
)

$ErrorActionPreference = 'Stop'
$revision = 'cbcc9fc825ba9aaf12304c7e9524643995faf4dd'
$source = (Resolve-Path -LiteralPath $ForkSource).Path
$destination = [IO.Path]::GetFullPath($OutputDir)
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
$vs = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if ($LASTEXITCODE -or !$vs) { throw 'Visual Studio x64 C++ tools are required.' }

$tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\')
$work = [IO.Directory]::CreateTempSubdirectory('openrevelare-pureneg-').FullName
try {
    $paths = 'LICENSE','vendor/film-dust/native/src','vendor/film-dust/native/node-api',
        'vendor/film-dust/LICENSE.txt','vendor/film-dust/NOTICES.txt'
    $tracked = & git -C $source ls-tree -r --name-only $revision -- @paths
    if ($LASTEXITCODE) { throw "The PURE Neg checkout must contain $revision." }
    foreach ($path in $tracked) {
        $target = Join-Path $work $path
        [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target)) | Out-Null
        & git -C $source cat-file blob ("${revision}:$path") > $target
        if ($LASTEXITCODE) { throw "Could not read $path from the pinned PURE Neg commit." }
    }

    Import-Module (Join-Path $vs 'Common7/Tools/Microsoft.VisualStudio.DevShell.dll')
    Enter-VsDevShell -VsInstallPath $vs -SkipAutomaticLocation -DevCmdArguments '-arch=x64 -host_arch=x64'
    Push-Location (Join-Path $work 'vendor/film-dust/native')
    try {
        $cpu = Join-Path (Get-Location).Path 'src/cpu.h'
        $header = Get-Content -LiteralPath $cpu -Raw
        $upstreamInline = '#define FD_INLINE inline __attribute__((always_inline))'
        if (!$header.Contains($upstreamInline)) { throw 'Unexpected PURE Neg CPU header.' }
        Set-Content -LiteralPath $cpu ($header.Replace($upstreamInline, '#define FD_INLINE __forceinline'))

        & lib.exe /NOLOGO /DEF:node-api/node_api.def /MACHINE:X64 /OUT:node_api.lib
        if ($LASTEXITCODE) { throw 'Could not generate the Node-API import library.' }
        $files = 'node_glue','filters','dustnet','maps','regions','pool','cues','soft','blobs','thin' |
            ForEach-Object { "src/$_.cpp" }
        & cl.exe /nologo /LD /MT /O2 /EHsc /std:c++17 /fp:strict /D_USE_MATH_DEFINES /Inode-api @files node_api.lib /link /OUT:filmdust.node
        if ($LASTEXITCODE) { throw 'Could not build the PURE Neg accelerator.' }

        New-Item -ItemType Directory -Force -Path $destination | Out-Null
        $addon = Join-Path $destination 'filmdust.node'
        Copy-Item -LiteralPath 'filmdust.node' -Destination $addon
        Copy-Item -LiteralPath (Join-Path $work 'LICENSE') -Destination (Join-Path $destination 'LICENSE-PURENeg.txt')
        Copy-Item -LiteralPath '../LICENSE.txt' -Destination (Join-Path $destination 'LICENSE-film-dust.txt')
        Copy-Item -LiteralPath '../NOTICES.txt' -Destination (Join-Path $destination 'NOTICES-film-dust.txt')
        @{
            revision = $revision
            platform = 'win-x64'
            sha256 = (Get-FileHash -LiteralPath $addon -Algorithm SHA256).Hash
        } | ConvertTo-Json | Set-Content -Encoding utf8 (Join-Path $destination 'manifest.json')
        Write-Output $destination
    }
    finally { Pop-Location }
}
finally {
    $resolvedWork = (Resolve-Path -LiteralPath $work).Path
    if ($resolvedWork.StartsWith($tempRoot + '\openrevelare-pureneg-', [StringComparison]::OrdinalIgnoreCase)) {
        Remove-Item -LiteralPath $resolvedWork -Recurse -Force
    }
}
