# OpenRevelare Win32 native presenter shim

This directory is an independent, x64-only C++17 DLL. It intentionally has no
dependency on Avalonia, Skia, LittleCMS, or the OpenRevelare C# projects, and it
is not currently added to `OpenRevelare.sln`.

Its only pixel operation is:

```text
caller-owned final presentation bytes
  -> CPU-writable D3D11 staging texture (Map + row memcpy + Unmap)
  -> same-size, same-format DXGI back buffer (CopyResource)
  -> Present
```

There are no shaders, render-target views, matrices, transfer functions, gamut
maps, ICC operations, overlay renderers, or implicit `_SRGB` texture views in
the shim.

The D3D device first binds to the physical adapter that owns the parent window's
monitor. It never silently picks a different physical GPU; if that exact adapter
cannot create a device, the shim may use the D3D11 WARP software adapter and
reports that choice in diagnostics. Display-id changes still recreate the
presenter and every frame remains bound to the current revision.

## ABI

Include `OpenRevelarePresentationWin32.h`. All exports use `extern "C"` and
`__cdecl`; handles are opaque. The supported lifecycle is:

1. `orwp_create(parentHwnd, mode, width, height, &presenter)`
2. zero or more `orwp_resize` and `orwp_present` calls
3. `orwp_query_diagnostics`
4. `orwp_destroy`

`parentHwnd` should be the dedicated native host/container for the preview.
The shim creates a visible child HWND at `(0, 0)` and fills the requested
physical-pixel size. Its `WM_NCHITTEST` result is `HTTRANSPARENT`, so the host
can route input to the shared C#/ViewModel viewport controller.

Create, resize, and destroy the presenter on the thread that owns the parent
HWND. Calls are internally serialized, but the caller must not race destroy
against another ABI call.

### Modes and byte layout

| ABI mode | DXGI format | DXGI color space | Input bytes |
|---|---|---|---|
| `ORWP_MODE_ADVANCED_COLOR` | `DXGI_FORMAT_R16G16B16A16_FLOAT` | `DXGI_COLOR_SPACE_RGB_FULL_G10_NONE_P709` | little-endian RGBA half, 8 bytes/pixel |
| `ORWP_MODE_LEGACY` | `DXGI_FORMAT_B8G8R8A8_UNORM` | implicit `DXGI_COLOR_SPACE_RGB_FULL_G22_NONE_P709` | final monitor-device BGRA8, or explicitly warned unmanaged-emergency sRGB BGRA8; 4 bytes/pixel |

Both modes use an HWND swap chain created by
`IDXGIFactory2::CreateSwapChainForHwnd` with two buffers and
`DXGI_SWAP_EFFECT_FLIP_DISCARD`.

Advanced mode calls `IDXGISwapChain3::CheckColorSpaceSupport`, requires
`DXGI_SWAP_CHAIN_COLOR_SPACE_SUPPORT_FLAG_PRESENT`, and then calls
`SetColorSpace1`. Creation fails with `ORWP_E_COLOR_SPACE` if that contract
cannot be established. The caller must select Advanced mode only after its
display environment reports the supported SDR-WCG Advanced Color contract.
The current phase deliberately routes HDR to the visibly warned emergency
BGRA8 contract until HDR/reference-white behavior is implemented and verified.

Legacy mode deliberately does not call `SetColorSpace1`. Its BGRA values must
either have received the one allowed shared-CMM monitor transform, or carry the
managed `UnmanagedEmergencySrgb8` contract (zero app transforms plus a visible
warning). The native mode never decides between those policies and never changes
the values; that distinction remains mandatory in the managed display/buffer
contract.

### Presentation contract validation

`orwp_present` receives both the frame's contract and the display
environment's current contract. Before `Map` it verifies all of the following:

- both contract structs have a supported size;
- frame mode, current mode, and presenter mode are identical;
- both UTF-8 display IDs are non-empty and byte-identical;
- frame revision equals the current contract revision;
- the revision is not older than the last successfully presented revision;
- after the first successful Present, the display ID remains bound to this
  presenter instance;
- width and height exactly equal the current swap-chain size;
- row pitch is at least the mode's tight row size;
- `byte_count` covers every copied row without integer overflow.

A display ID change or presentation mode change requires destroying and
recreating the presenter. A greater revision for the same display and mode is
accepted after `orwp_resize`/presentation rebuilding as needed. Rejected calls
do not Map or upload the caller's bytes.

`display_id_utf8` is borrowed only for the call. IDs are compared as canonical
UTF-8 bytes; canonicalization belongs to the display environment. The first
255 bytes of the last accepted ID are copied into diagnostics, with a
truncation flag.

### Diagnostics

Initialize `OrwpDiagnostics.struct_size` to `sizeof(OrwpDiagnostics)` and call
`orwp_query_diagnostics`. The snapshot includes:

- ABI version, mode, current dimensions, child HWND;
- selected adapter LUID and D3D feature level;
- DXGI format, declared color space, support flags, and whether SetColorSpace
  was called;
- HRESULTs from device/swap-chain creation, color-space check/set, resize,
  staging Map, and Present;
- `ID3D11Device::GetDeviceRemovedReason` when applicable;
- accepted/rejected Present counts and last accepted display/revision.

The diagnostics prove the native surface/upload contract; they do not prove
the Windows display environment's Advanced Color or ICC state. That evidence
belongs to the C# `IDisplayEnvironment` implementation and must be passed as
the current presentation contract.

## Build

The application-owned assembly path is the repository script:

```powershell
./packaging/windows/build-win32-presenter.ps1
```

It locates Visual Studio 2022 MSBuild, builds Release|x64 twice in isolated
temporary output trees with `/Brepro` and no PDB, rejects differing hashes,
links the C/C++ runtime statically (`/MT`) so the app does not depend on an
unbundled VC++ Redistributable, and writes the verified DLL plus its
ABI/source/artifact manifest to
`native/win-x64`.

The project remains directly buildable for development:

```powershell
msbuild .\OpenRevelare.Presentation.Win32.Native.vcxproj `
  /m:1 /p:Configuration=Release /p:Platform=x64
```

Output:

```text
artifacts/x64/Release/OpenRevelare.Presentation.Win32.Native.dll
```

Intermediate and output directories are ignored locally. The reproducible
packaging path pins Windows SDK 10.0.26100.0 and the Visual Studio 2022 `v143`
toolset.
