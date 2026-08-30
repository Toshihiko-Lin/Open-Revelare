[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$Directory
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$libraryPath = [IO.Path]::GetFullPath((Join-Path $Directory "OpenRevelare.Presentation.Win32.Native.dll"))
if (-not (Test-Path -LiteralPath $libraryPath -PathType Leaf)) {
    throw "Win32 presenter smoke DLL is missing: $libraryPath"
}

$source = @'
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace OpenRevelarePackaging
{
    public static class NativePresenterSmoke
    {
        private const uint ModeAdvanced = 1;
        private const uint ModeLegacy = 2;
        private const int ResultOk = 0;
        private const int ResultColorSpace = -11;
        private const uint FormatR16G16B16A16Float = 10;
        private const uint FormatB8G8R8A8Unorm = 87;
        private const uint ColorSpaceG22P709 = 0;
        private const uint ColorSpaceG10P709 = 1;
        private const uint ColorSpacePresentSupport = 1;
        private const uint D3DDriverTypeWarp = 5;
        private const uint D3D11SdkVersion = 7;
        private const uint D3D11CreateDeviceBgraSupport = 0x20;
        private const uint WsOverlappedWindow = 0x00CF0000;
        private const uint LoadLibrarySearchDllLoadDir = 0x00000100;
        private const uint LoadLibrarySearchSystem32 = 0x00000800;
        private const uint FrameWidth = 256;
        private const uint FrameHeight = 256;
        private const int WarmupPresentCount = 5;
        private const int TimedPresentCount = 21;

        [StructLayout(LayoutKind.Sequential)]
        private struct PresentationContract
        {
            public uint StructSize;
            public uint Mode;
            public IntPtr DisplayIdUtf8;
            public ulong Revision;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct Diagnostics
        {
            public uint StructSize;
            public uint AbiVersion;
            public int LastResult;
            public uint Mode;
            public uint Width;
            public uint Height;
            public uint DxgiFormat;
            public uint DxgiColorSpace;
            public uint ColorSpaceSupport;
            public uint ColorSpaceWasSet;
            public uint FeatureLevel;
            public uint UsingWarp;
            public int CreateDeviceHr;
            public int CreateSwapChainHr;
            public int CheckColorSpaceHr;
            public int SetColorSpaceHr;
            public int ResizeBuffersHr;
            public int MapHr;
            public int PresentHr;
            public int DeviceRemovedReason;
            public uint AdapterLuidLow;
            public int AdapterLuidHigh;
            public ulong ChildHwnd;
            public ulong SuccessfulPresentCount;
            public ulong RejectedPresentCount;
            public ulong LastContractRevision;
            public uint DisplayIdWasTruncated;

            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 256)]
            public byte[] LastDisplayIdUtf8;
        }

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int CreatePresenter(
            IntPtr parentHwnd,
            uint mode,
            uint width,
            uint height,
            out IntPtr presenter);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int ResizePresenter(IntPtr presenter, uint width, uint height);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int PresentFrame(
            IntPtr presenter,
            IntPtr bytes,
            UIntPtr byteCount,
            uint rowPitch,
            uint width,
            uint height,
            ref PresentationContract frameContract,
            ref PresentationContract currentContract);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int QueryDiagnostics(IntPtr presenter, ref Diagnostics diagnostics);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void DestroyPresenter(IntPtr presenter);

        public static string Run(string libraryPath)
        {
            string warp = VerifyWarpDevice();
            IntPtr window = CreateWindowExW(
                0,
                "STATIC",
                "OpenRevelare native presenter smoke",
                WsOverlappedWindow,
                0,
                0,
                96,
                96,
                IntPtr.Zero,
                IntPtr.Zero,
                GetModuleHandleW(null),
                IntPtr.Zero);
            if (window == IntPtr.Zero)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not create hidden smoke HWND.");

            IntPtr module = IntPtr.Zero;
            try
            {
                module = LoadLibraryExW(
                    Path.GetFullPath(libraryPath),
                    IntPtr.Zero,
                    LoadLibrarySearchDllLoadDir | LoadLibrarySearchSystem32);
                if (module == IntPtr.Zero)
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not load assembled presenter DLL.");

                CreatePresenter create = GetExport<CreatePresenter>(module, "orwp_create");
                ResizePresenter resize = GetExport<ResizePresenter>(module, "orwp_resize");
                PresentFrame present = GetExport<PresentFrame>(module, "orwp_present");
                QueryDiagnostics query = GetExport<QueryDiagnostics>(module, "orwp_query_diagnostics");
                DestroyPresenter destroy = GetExport<DestroyPresenter>(module, "orwp_destroy");

                string legacy = RunMode(
                    window,
                    ModeLegacy,
                    FormatB8G8R8A8Unorm,
                    ColorSpaceG22P709,
                    create,
                    resize,
                    present,
                    query,
                    destroy);

                string advanced;
                try
                {
                    advanced = RunMode(
                        window,
                        ModeAdvanced,
                        FormatR16G16B16A16Float,
                        ColorSpaceG10P709,
                        create,
                        resize,
                        present,
                        query,
                        destroy);
                }
                catch (NativeCreateException error)
                {
                    if (error.Result != ResultColorSpace)
                        throw;
                    advanced = "Advanced FP16/scRGB unsupported by this display path (ORWP_E_COLOR_SPACE)";
                }

                return warp + "; " + legacy + "; " + advanced +
                    "; timing=native upload+Present wall only, warm-up/create and CPU compose/pack excluded";
            }
            finally
            {
                if (module != IntPtr.Zero)
                    FreeLibrary(module);
                DestroyWindow(window);
            }
        }

        private static string RunMode(
            IntPtr window,
            uint mode,
            uint expectedFormat,
            uint expectedColorSpace,
            CreatePresenter create,
            ResizePresenter resize,
            PresentFrame present,
            QueryDiagnostics query,
            DestroyPresenter destroy)
        {
            IntPtr presenter;
            int result = create(window, mode, 16, 16, out presenter);
            if (result != ResultOk)
                throw new NativeCreateException(mode, result);
            if (presenter == IntPtr.Zero)
                throw new InvalidOperationException("Native create succeeded with a null presenter.");

            try
            {
                result = resize(presenter, FrameWidth, FrameHeight);
                RequireResult(result, "resize", mode);

                uint bytesPerPixel = mode == ModeAdvanced ? 8u : 4u;
                uint rowPitch = FrameWidth * bytesPerPixel;
                byte[] frame = new byte[checked((int)(rowPitch * FrameHeight))];
                int pixelCount = checked((int)(FrameWidth * FrameHeight));
                for (int pixel = 0; pixel < pixelCount; pixel++)
                {
                    if (mode == ModeAdvanced)
                    {
                        // RGBA16F opaque black with half(1.0) alpha.
                        frame[pixel * 8 + 6] = 0x00;
                        frame[pixel * 8 + 7] = 0x3C;
                    }
                    else
                    {
                        frame[pixel * 4 + 3] = 0xFF;
                    }
                }

                IntPtr frameBytes = Marshal.AllocHGlobal(frame.Length);
                IntPtr displayId = AllocateUtf8("win32:native-smoke");
                double medianMilliseconds;
                try
                {
                    Marshal.Copy(frame, 0, frameBytes, frame.Length);
                    PresentationContract contract = new PresentationContract();
                    contract.StructSize = (uint)Marshal.SizeOf(typeof(PresentationContract));
                    contract.Mode = mode;
                    contract.DisplayIdUtf8 = displayId;
                    contract.Revision = 7;
                    for (int index = 0; index < WarmupPresentCount; index++)
                    {
                        result = present(
                            presenter,
                            frameBytes,
                            new UIntPtr((uint)frame.Length),
                            rowPitch,
                            FrameWidth,
                            FrameHeight,
                            ref contract,
                            ref contract);
                        RequireResult(result, "warm-up present", mode);
                    }

                    double[] samples = new double[TimedPresentCount];
                    for (int index = 0; index < samples.Length; index++)
                    {
                        long started = Stopwatch.GetTimestamp();
                        result = present(
                            presenter,
                            frameBytes,
                            new UIntPtr((uint)frame.Length),
                            rowPitch,
                            FrameWidth,
                            FrameHeight,
                            ref contract,
                            ref contract);
                        long finished = Stopwatch.GetTimestamp();
                        RequireResult(result, "timed present", mode);
                        samples[index] =
                            (finished - started) * 1000.0 / Stopwatch.Frequency;
                    }
                    Array.Sort(samples);
                    medianMilliseconds = samples[samples.Length / 2];
                }
                finally
                {
                    Marshal.FreeHGlobal(displayId);
                    Marshal.FreeHGlobal(frameBytes);
                }

                Diagnostics diagnostics = new Diagnostics();
                diagnostics.StructSize = (uint)Marshal.SizeOf(typeof(Diagnostics));
                diagnostics.LastDisplayIdUtf8 = new byte[256];
                result = query(presenter, ref diagnostics);
                RequireResult(result, "query diagnostics", mode);
                ulong expectedPresents = (ulong)(WarmupPresentCount + TimedPresentCount);
                ValidateDiagnostics(
                    diagnostics,
                    mode,
                    expectedFormat,
                    expectedColorSpace,
                    expectedPresents);

                return string.Format(
                    CultureInfo.InvariantCulture,
                    "{0} present OK (format={1}, colorSpace={2}, WARP={3}, presents={4}, " +
                    "medianNativeUploadPresentMs={5:F4}, warmup={6}, samples={7}, size={8}x{9})",
                    mode == ModeAdvanced ? "Advanced FP16/scRGB" : "Legacy BGRA8 passthrough",
                    diagnostics.DxgiFormat,
                    diagnostics.DxgiColorSpace,
                    diagnostics.UsingWarp,
                    diagnostics.SuccessfulPresentCount,
                    medianMilliseconds,
                    WarmupPresentCount,
                    TimedPresentCount,
                    FrameWidth,
                    FrameHeight);
            }
            finally
            {
                destroy(presenter);
            }
        }

        private static void ValidateDiagnostics(
            Diagnostics value,
            uint mode,
            uint expectedFormat,
            uint expectedColorSpace,
            ulong expectedPresents)
        {
            Require(value.AbiVersion == 1, "diagnostics ABI version is not 1");
            Require(value.LastResult == ResultOk, "diagnostics last_result is not ORWP_OK");
            Require(value.Mode == mode, "diagnostics mode mismatch");
            Require(
                value.Width == FrameWidth && value.Height == FrameHeight,
                "resize dimensions were not retained");
            Require(value.DxgiFormat == expectedFormat, "DXGI format mismatch");
            Require(value.DxgiColorSpace == expectedColorSpace, "DXGI color-space mismatch");
            Require(value.FeatureLevel != 0, "D3D feature level was not reported");
            Require(value.UsingWarp <= 1, "using_warp is not boolean");
            Require(value.CreateDeviceHr >= 0, "D3D11CreateDevice failed");
            Require(value.CreateSwapChainHr >= 0, "CreateSwapChainForHwnd failed");
            Require(value.ResizeBuffersHr >= 0, "ResizeBuffers failed");
            Require(value.MapHr >= 0, "staging texture Map failed");
            Require(value.PresentHr >= 0, "DXGI Present failed");
            Require(value.ChildHwnd != 0, "presenter child HWND was not reported");
            Require(
                value.SuccessfulPresentCount == expectedPresents,
                "successful present count does not include every warm-up/timed frame");
            Require(value.RejectedPresentCount == 0, "a smoke frame was rejected");
            Require(value.LastContractRevision == 7, "contract revision was not retained");
            Require(value.DisplayIdWasTruncated == 0, "smoke display id was truncated");
            Require(ReadUtf8(value.LastDisplayIdUtf8) == "win32:native-smoke", "display id mismatch");

            if (mode == ModeAdvanced)
            {
                Require(value.ColorSpaceWasSet == 1, "Advanced color space was not declared");
                Require(
                    (value.ColorSpaceSupport & ColorSpacePresentSupport) != 0,
                    "Advanced color space lacks PRESENT support");
                Require(value.CheckColorSpaceHr >= 0, "CheckColorSpaceSupport failed");
                Require(value.SetColorSpaceHr >= 0, "SetColorSpace1 failed");
            }
            else
            {
                Require(value.ColorSpaceWasSet == 0, "Legacy passthrough declared a source color space");
            }
        }

        private static string VerifyWarpDevice()
        {
            uint[] levels = new uint[] { 0xB100, 0xB000, 0xA100, 0xA000 };
            IntPtr device;
            IntPtr context;
            uint selected;
            int hr = D3D11CreateDevice(
                IntPtr.Zero,
                D3DDriverTypeWarp,
                IntPtr.Zero,
                D3D11CreateDeviceBgraSupport,
                levels,
                (uint)levels.Length,
                D3D11SdkVersion,
                out device,
                out selected,
                out context);
            if (hr < 0)
                Marshal.ThrowExceptionForHR(hr);
            try
            {
                Require(device != IntPtr.Zero && context != IntPtr.Zero, "WARP returned null D3D objects");
                return string.Format("D3D11 WARP available (featureLevel=0x{0:X})", selected);
            }
            finally
            {
                if (context != IntPtr.Zero) Marshal.Release(context);
                if (device != IntPtr.Zero) Marshal.Release(device);
            }
        }

        private static T GetExport<T>(IntPtr module, string name) where T : class
        {
            IntPtr address = GetProcAddress(module, name);
            if (address == IntPtr.Zero)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Missing native export " + name + ".");
            T value = Marshal.GetDelegateForFunctionPointer(address, typeof(T)) as T;
            if (value == null)
                throw new InvalidOperationException("Could not bind native export " + name + ".");
            return value;
        }

        private static IntPtr AllocateUtf8(string text)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(text + "\0");
            IntPtr pointer = Marshal.AllocHGlobal(bytes.Length);
            Marshal.Copy(bytes, 0, pointer, bytes.Length);
            return pointer;
        }

        private static string ReadUtf8(byte[] bytes)
        {
            if (bytes == null) return string.Empty;
            int length = Array.IndexOf<byte>(bytes, 0);
            if (length < 0) length = bytes.Length;
            return Encoding.UTF8.GetString(bytes, 0, length);
        }

        private static void RequireResult(int result, string operation, uint mode)
        {
            if (result != ResultOk)
                throw new InvalidOperationException(string.Format(
                    "Native {0} failed for mode {1}: result {2}.", operation, mode, result));
        }

        private static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException("Native smoke assertion failed: " + message + ".");
        }

        private sealed class NativeCreateException : InvalidOperationException
        {
            public readonly int Result;

            public NativeCreateException(uint mode, int result)
                : base(string.Format("Native create failed for mode {0}: result {1}.", mode, result))
            {
                Result = result;
            }
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr LoadLibraryExW(string fileName, IntPtr file, uint flags);

        [DllImport("kernel32.dll", CharSet = CharSet.Ansi, ExactSpelling = true, SetLastError = true)]
        private static extern IntPtr GetProcAddress(IntPtr module, string name);

        [DllImport("kernel32.dll", ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool FreeLibrary(IntPtr module);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr GetModuleHandleW(string moduleName);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateWindowExW(
            uint extendedStyle,
            string className,
            string windowName,
            uint style,
            int x,
            int y,
            int width,
            int height,
            IntPtr parent,
            IntPtr menu,
            IntPtr instance,
            IntPtr parameter);

        [DllImport("user32.dll", ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DestroyWindow(IntPtr hwnd);

        [DllImport("d3d11.dll", ExactSpelling = true)]
        private static extern int D3D11CreateDevice(
            IntPtr adapter,
            uint driverType,
            IntPtr software,
            uint flags,
            [In] uint[] featureLevels,
            uint featureLevelCount,
            uint sdkVersion,
            out IntPtr device,
            out uint selectedFeatureLevel,
            out IntPtr immediateContext);
    }
}
'@

$smokeType = "OpenRevelarePackaging.NativePresenterSmoke" -as [type]
if ($null -eq $smokeType) {
    $compiled = @(Add-Type -TypeDefinition $source -Language CSharp -PassThru)
    $smokeType = $compiled | Where-Object FullName -eq "OpenRevelarePackaging.NativePresenterSmoke"
    if ($null -eq $smokeType) { throw "Could not compile the Win32 presenter smoke harness." }
}

$result = $smokeType::Run($libraryPath)
Write-Host "Native D3D smoke: $result"
