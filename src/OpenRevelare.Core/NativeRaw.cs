using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace OpenRevelare.Core.NativeRaw;

// Only primitives and flat arrays cross this ABI; no libraw_data_t offsets or marshalled
// LibRaw structs. The native bridge is compiled against the pinned upstream headers.
internal static class Native
{
    private const string Library = "OpenRevelare.Raw.Native";
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr or_raw_version();
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr or_raw_create();
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern void or_raw_close(IntPtr raw);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int or_raw_open(RawHandle raw, IntPtr data, nuint size);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int or_raw_open_file(RawHandle raw, [MarshalAs(UnmanagedType.LPUTF8Str)] string path);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int or_raw_open_file_wide(RawHandle raw, [MarshalAs(UnmanagedType.LPWStr)] string path);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int or_raw_unpack(RawHandle raw);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr or_raw_error(int error);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int or_raw_value(RawHandle raw, int key);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr or_raw_model(RawHandle raw, int model);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern void or_raw_matrix(RawHandle raw, [Out] float[] values);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern void or_raw_wb(RawHandle raw, [Out] float[] values);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern void or_raw_crop(RawHandle raw, [Out] int[] values);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int or_raw_process(RawHandle raw, int fbdd, int half, uint[]? crop);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr or_raw_image(RawHandle raw, out int error);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern void or_raw_image_free(IntPtr image);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr or_raw_image_data(ImageHandle image, [Out] int[] info);
    internal static string Version => Marshal.PtrToStringAnsi(or_raw_version()) ?? "unknown";
    internal static void Check(int error)
    {
        if (error != 0) throw new LibRawException(error);
    }
}

internal sealed class LibRawException(int code)
    : Exception($"LibRaw ({code}): {Marshal.PtrToStringAnsi(Native.or_raw_error(code))}");

internal sealed class RawHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    private GCHandle _buffer;
    internal RawHandle(byte[]? bytes = null) : base(true)
    {
        SetHandle(Native.or_raw_create());
        if (IsInvalid) throw new OutOfMemoryException("LibRaw context allocation failed");
        try { if (bytes is not null) _buffer = GCHandle.Alloc(bytes, GCHandleType.Pinned); }
        catch { Dispose(); throw; }
    }
    internal IntPtr Buffer => _buffer.AddrOfPinnedObject();
    protected override bool ReleaseHandle()
    {
        Native.or_raw_close(handle);
        if (_buffer.IsAllocated) _buffer.Free();
        return true;
    }
}

internal sealed class ImageHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    internal ImageHandle(IntPtr image) : base(true) => SetHandle(image);
    protected override bool ReleaseHandle() { Native.or_raw_image_free(handle); return true; }
}

internal sealed class RawContext : IDisposable
{
    private readonly RawHandle _raw;
    private RawContext(byte[]? bytes, string? path = null)
    {
        if (Native.Version != "0.22.2-Release")
            throw new NotSupportedException($"Expected LibRaw 0.22.2, loaded {Native.Version}");
        _raw = new RawHandle(bytes);
        try
        {
            int error = bytes is not null ? Native.or_raw_open(_raw, _raw.Buffer, (nuint)bytes.Length)
                : OperatingSystem.IsWindows() ? Native.or_raw_open_file_wide(_raw, path!)
                : Native.or_raw_open_file(_raw, path!);
            Native.Check(error);
        }
        catch { _raw.Dispose(); throw; }
    }
    public static RawContext FromBuffer(byte[] bytes) => new(bytes);
    public static RawContext OpenFile(string path) => new(null, path);
    public int Width => Native.or_raw_value(_raw, 0);
    public int Height => Native.or_raw_value(_raw, 1);
    public int RawWidth => Native.or_raw_value(_raw, 2);
    public int RawHeight => Native.or_raw_value(_raw, 3);
    public int ColorMaximum => Native.or_raw_value(_raw, 4);
    public (string Make, string Model) ImageParams => (
        Marshal.PtrToStringAnsi(Native.or_raw_model(_raw, 0)) ?? "",
        Marshal.PtrToStringAnsi(Native.or_raw_model(_raw, 1)) ?? "");
    public float[,] RgbCamera
    {
        get
        {
            var values = new float[12]; Native.or_raw_matrix(_raw, values);
            var result = new float[3, 4];
            Buffer.BlockCopy(values, 0, result, 0, 48);
            return result;
        }
    }
    public float[] CameraMultipler
    {
        get { var values = new float[4]; Native.or_raw_wb(_raw, values); return values; }
    }
    public System.Drawing.Rectangle? InsetCrop
    {
        get
        {
            var c = new int[4]; Native.or_raw_crop(_raw, c);
            if (c[0] == 65535 || c[1] == 65535 || c[2] <= 0 || c[3] <= 0
                || c[0] + c[2] > RawWidth || c[1] + c[3] > RawHeight) return null;
            if (c[0] == 0 && c[1] == 0 && c[2] == RawWidth && c[3] == RawHeight) return null;
            return new(c[0], c[1], c[2], c[3]);
        }
    }
    public void Unpack() => Native.Check(Native.or_raw_unpack(_raw));
    public void Process(int fbdd, bool halfSize, System.Drawing.Rectangle? crop)
    {
        uint[]? box = crop is { } c ? [(uint)c.X, (uint)c.Y, (uint)c.Width, (uint)c.Height] : null;
        Native.Check(Native.or_raw_process(_raw, fbdd, halfSize ? 1 : 0, box));
    }
    public ProcessedImage MakeDcrawMemoryImage()
    {
        var pointer = Native.or_raw_image(_raw, out int error);
        var image = new ImageHandle(pointer);
        try
        {
            Native.Check(error);
            if (image.IsInvalid) throw new InvalidOperationException("LibRaw returned no image");
            return new ProcessedImage(image);
        }
        catch { image.Dispose(); throw; }
    }
    public void Dispose() => _raw.Dispose();
}

internal sealed class ProcessedImage : IDisposable
{
    private readonly ImageHandle _image;
    private readonly IntPtr _data;
    private readonly int _bytes;
    public int Width { get; }
    public int Height { get; }
    public int Channels { get; }
    public int Bits { get; }
    internal ProcessedImage(ImageHandle image)
    {
        _image = image;
        var info = new int[6]; _data = Native.or_raw_image_data(image, info);
        (Width, Height, Channels, Bits, _bytes) = (info[0], info[1], info[2], info[3], info[4]);
        if (info[5] != 2 || Width <= 0 || Height <= 0 || Channels is not (1 or 3 or 4)
            || Bits != 16 || _bytes != checked(Width * Height * Channels * 2))
            throw new NotSupportedException("LibRaw returned an invalid 16-bit bitmap");
    }
    public unsafe ReadOnlySpan<T> AsSpan<T>() where T : unmanaged
    {
        ObjectDisposedException.ThrowIf(_image.IsClosed, this);
        return new ReadOnlySpan<T>(_data.ToPointer(), _bytes / sizeof(T));
    }
    public void Dispose() => _image.Dispose();
}
