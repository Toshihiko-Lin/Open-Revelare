using System.Runtime.InteropServices;

namespace OpenRevelare.Presentation.Win32.Interop;

/// <summary>
/// What the panel behind one monitor can physically reproduce, as DXGI reports it.
///
/// <para>
/// WHY THIS IS A SEPARATE SOURCE FROM DisplayConfig. Everything else the probe reads comes from
/// <c>DisplayConfigGetDeviceInfo</c>, which knows the composition MODE (WCG/HDR) and the user's
/// SDR white, but nothing about the panel's luminance range. That range lives only in
/// <c>IDXGIOutput6::GetDesc1</c>, populated from the monitor's EDID/DisplayID or from a run of
/// the Windows HDR Calibration app. Without it the contract's headroom is a placeholder, and the
/// application cannot say where the screen stops showing what the render is putting above
/// diffuse white — which, on a DisplayHDR-400-class panel, is not far above it at all.
/// </para>
/// </summary>
internal readonly record struct DisplayLuminance(
    float MinNits,
    float MaxNits,
    float MaxFullFrameNits,
    uint BitsPerColor);

/// <summary>
/// Raw-vtable access to the DXGI output that owns a given <c>HMONITOR</c>.
///
/// <para>
/// HAND-WRITTEN COM ON PURPOSE. This assembly deliberately carries no COM interop library — the
/// native presenter is a C ABI shim precisely so that managed code never has to own DXGI object
/// lifetimes — and one read-only query does not justify importing one. The calls below are the
/// three vtable hops the query needs and nothing else; every interface pointer acquired is
/// released on every path.
/// </para>
/// </summary>
internal static unsafe class DxgiOutputInterop
{
    private static readonly Guid IidFactory1 = new("770aae78-f26f-4dba-a829-253c83d1b387");
    private static readonly Guid IidOutput6 = new("068346e8-aaec-4b84-add7-137f513f77a1");

    private const int DxgiErrorNotFound = unchecked((int)0x887A0002);

    // IUnknown: 0 QueryInterface, 1 AddRef, 2 Release. IDXGIObject adds 3..6.
    private const int VtblRelease = 2;
    private const int VtblQueryInterface = 0;
    private const int VtblFactory1EnumAdapters1 = 12;
    private const int VtblAdapterEnumOutputs = 7;
    private const int VtblOutput6GetDesc1 = 27;

    /// <summary>
    /// Reads the luminance description of the output that owns <paramref name="monitor"/>.
    /// Returns null, with the reason, when DXGI cannot produce one — a software adapter, a remote
    /// session, an output that pre-dates <c>IDXGIOutput6</c>.
    /// </summary>
    internal static (DisplayLuminance? Luminance, string? Failure) Read(nint monitor)
    {
        if (monitor == nint.Zero) return (null, "No monitor handle.");

        nint factory = nint.Zero;
        try
        {
            int hr = CreateDXGIFactory1(in IidFactory1, out factory);
            if (hr < 0 || factory == nint.Zero)
                return (null, $"CreateDXGIFactory1 failed (0x{hr:X8}).");

            for (uint adapterIndex = 0; ; adapterIndex++)
            {
                nint adapter;
                hr = ((delegate* unmanaged[Stdcall]<nint, uint, nint*, int>)Slot(factory, VtblFactory1EnumAdapters1))(
                    factory, adapterIndex, &adapter);
                if (hr == DxgiErrorNotFound) break;
                if (hr < 0) return (null, $"EnumAdapters1 failed (0x{hr:X8}).");

                try
                {
                    (DisplayLuminance? found, string? failure) = ScanAdapter(adapter, monitor);
                    if (found is not null || failure is not null) return (found, failure);
                }
                finally
                {
                    Release(adapter);
                }
            }

            return (null, "No DXGI output owns this monitor.");
        }
        finally
        {
            if (factory != nint.Zero) Release(factory);
        }
    }

    private static (DisplayLuminance?, string?) ScanAdapter(nint adapter, nint monitor)
    {
        for (uint outputIndex = 0; ; outputIndex++)
        {
            nint output;
            int hr = ((delegate* unmanaged[Stdcall]<nint, uint, nint*, int>)Slot(adapter, VtblAdapterEnumOutputs))(
                adapter, outputIndex, &output);
            if (hr == DxgiErrorNotFound) return (null, null);
            if (hr < 0) return (null, $"EnumOutputs failed (0x{hr:X8}).");

            try
            {
                nint output6;
                Guid iid = IidOutput6;
                hr = ((delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)Slot(output, VtblQueryInterface))(
                    output, &iid, &output6);
                if (hr < 0 || output6 == nint.Zero) continue;

                try
                {
                    DxgiOutputDesc1 desc = default;
                    hr = ((delegate* unmanaged[Stdcall]<nint, DxgiOutputDesc1*, int>)Slot(output6, VtblOutput6GetDesc1))(
                        output6, &desc);
                    if (hr < 0) return (null, $"IDXGIOutput6::GetDesc1 failed (0x{hr:X8}).");
                    if (desc.Monitor != monitor) continue;

                    return (new DisplayLuminance(
                        desc.MinLuminance,
                        desc.MaxLuminance,
                        desc.MaxFullFrameLuminance,
                        desc.BitsPerColor), null);
                }
                finally
                {
                    Release(output6);
                }
            }
            finally
            {
                Release(output);
            }
        }
    }

    private static nint Slot(nint comObject, int index) => (*(nint**)comObject)[index];

    private static void Release(nint comObject) =>
        ((delegate* unmanaged[Stdcall]<nint, uint>)Slot(comObject, VtblRelease))(comObject);

    [DllImport("dxgi.dll", ExactSpelling = true)]
    private static extern int CreateDXGIFactory1(in Guid riid, out nint factory);

    /// <summary>
    /// <c>DXGI_OUTPUT_DESC1</c>, laid out exactly as dxgi1_6.h declares it. The test project pins
    /// the managed size against the SDK's.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct DxgiOutputDesc1
    {
        public fixed char DeviceName[32];
        public int Left, Top, Right, Bottom;
        public int AttachedToDesktop;
        public uint Rotation;
        public nint Monitor;
        public uint BitsPerColor;
        public uint ColorSpace;
        public float RedPrimaryX, RedPrimaryY;
        public float GreenPrimaryX, GreenPrimaryY;
        public float BluePrimaryX, BluePrimaryY;
        public float WhitePointX, WhitePointY;
        public float MinLuminance;
        public float MaxLuminance;
        public float MaxFullFrameLuminance;
    }
}
