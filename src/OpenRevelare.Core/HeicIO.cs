using System.Runtime.InteropServices;
using System.Text;
using OpenRevelare.ColorManagement;

namespace OpenRevelare.Core;

/// <summary>
/// HEVC Main10 delivery. SDR carries its exact ICC; HDR carries BT.2020/PQ NCLX,
/// never the linear carrier's ICC, which would describe different pixel values.
/// </summary>
public static class HeicIO
{
    private static readonly object HardwareEncoderGate = new();
    private static string _encoderDiagnostics = "not initialized";
    /// <summary>The last completed encode, including any hardware fallback reason.</summary>
    public static string EncoderDiagnostics => Volatile.Read(ref _encoderDiagnostics);

    public static JpegIO.JpegFit? ExportHeic(
        RenderedFrame frame, string path, int quality = 90, long? maxBytes = null, SourceExif? sourceExif = null)
    {
        ArgumentNullException.ThrowIfNull(frame);
        bool hdr = frame.Encoding.Range == NumericRange.Extended;
        // Validate before allocating encoder buffers or touching an existing destination.
        Validate(frame, hdr);
        byte[]? profile = hdr ? null : ExportColorPolicy.ResolveProfileBytes(frame, ExportProfilePolicy.EmbedExact);
        if (maxBytes is { } ceiling)
        {
            RenderedFrame scaled = frame;
            ushort[]? pixels = null;
            var (bytes, fit) = JpegIO.FitToSize(frame.Pixels.Width, frame.Pixels.Height, quality, ceiling, (edge, q) =>
            {
                if (Math.Max(scaled.Pixels.Width, scaled.Pixels.Height) > edge)
                {
                    scaled = frame.WithPixels(Resample.ToLongEdge(frame.Pixels, edge, allowUpscale: false));
                    pixels = null;
                }
                // Quality probes share the same quantized pixels, including the expensive
                // HDR gamut/PQ conversion. Only a resolution change invalidates them.
                pixels ??= PreparePixels(scaled);
                return Encode(scaled, pixels, hdr, q, profile, sourceExif);
            }, formatName: "HEIC");
            ExportFile.Write(path, destination => File.WriteAllBytes(destination, bytes));
            return fit;
        }
        byte[] file = Encode(frame, PreparePixels(frame), hdr, quality, profile, sourceExif);
        ExportFile.Write(path, destination => File.WriteAllBytes(destination, file));
        return null;
    }

    private static void Validate(RenderedFrame frame, bool hdr)
    {
        if (hdr)
        {
            if (frame.Encoding.Reference != ColorReference.SceneReferred
                || frame.Encoding.Transfer != TransferState.LinearInProfilePrimaries
                || frame.OutputProfile.Identity != BuiltInColorProfiles.LinearExtendedSrgb(ProfileRole.Output).Identity)
                throw new NotSupportedException("HDR HEIC requires the canonical linear extended sRGB carrier.");
        }
        else if (frame.Encoding.Reference != ColorReference.DisplayReferred
            || frame.Encoding.Transfer != TransferState.ProfileEncoded
            || frame.Encoding.Range != NumericRange.Normalized)
            throw new NotSupportedException("SDR HEIC requires normalized, display-referred, profile-encoded pixels.");
    }

    /// <summary>Quantization happens after converting extended sRGB to the PQ container gamut.</summary>
    public static ushort[] PreparePixels(RenderedFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        bool hdr = frame.Encoding.Range == NumericRange.Extended;
        Validate(frame, hdr);
        float[] values = frame.Pixels.Data;
        var result = new ushort[values.Length];
        if (hdr)
        {
            // Avoid another full-resolution float RGB image (~960 MB at 80 MP).
            // Each worker converts a small scratch buffer using the same gamut math.
            ParallelSweep.OverPixels(frame.Pixels.PixelCount, (from, to) =>
            {
                const int chunkSamples = 65535; // RGB aligned; inner sweeps stay serial.
                var scratch = new float[Math.Min(chunkSamples, to - from)];
                for (int start = from; start < to; start += scratch.Length)
                {
                    int count = Math.Min(scratch.Length, to - start);
                    values.AsSpan(start, count).CopyTo(scratch);
                    HdrGainMap.ConvertHdrToBaseGamutInPlace(scratch, ColorSpaces.Rec2020);
                    for (int i = 0; i < count; i++)
                    {
                        float v = float.IsFinite(scratch[i]) ? scratch[i] : 0f;
                        v = Pq.FromNits(v * OutputTarget.ReferenceWhiteNits);
                        result[start + i] = (ushort)Math.Clamp((int)(Math.Clamp(v, 0f, 1f) * 1023f + 0.5f), 0, 1023);
                    }
                }
            });
            return result;
        }
        ParallelSweep.Over(values.Length, (from, to) =>
        {
            for (int i = from; i < to; i++)
            {
                float v = values[i];
                if (!float.IsFinite(v)) v = 0f;
                result[i] = (ushort)Math.Clamp((int)(Math.Clamp(v, 0f, 1f) * 1023f + 0.5f), 0, 1023);
            }
        });
        return result;
    }

    private static byte[] Encode(RenderedFrame frame, ushort[] pixels, bool hdr, int quality, byte[]? profile, SourceExif? sourceExif)
    {
        byte[]? exif = sourceExif?.ToByteArray(frame.Pixels.Width, frame.Pixels.Height,
            exactSrgb: !hdr && frame.OutputProfile.Identity == BuiltInColorProfiles.For(ColorSpaces.Srgb, ProfileRole.Output).Identity);
        byte[]? xmp = sourceExif?.MergeXmp(null);
        var error = new StringBuilder(1024);
        IntPtr output = IntPtr.Zero;
        bool hardwareLease = GpuAcceleration.Mode != GpuAccelerationMode.Off
            && (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS())
            && Monitor.TryEnter(HardwareEncoderGate);
        try
        {
            int status, usedHardware = 0;
            nuint size;
            if (hardwareLease)
            {
                try
                {
                    status = Native.or_heif_encode_exif_accelerated(pixels, frame.Pixels.Width, frame.Pixels.Height,
                        Math.Clamp(quality, 1, 100), hdr ? 1 : 0, profile, profile?.Length ?? 0,
                        exif, exif?.Length ?? 0, xmp, xmp?.Length ?? 0, 1, out usedHardware,
                        out output, out size, error, error.Capacity);
                }
                catch (EntryPointNotFoundException)
                {
                    // Older native payloads remain usable after an application-only update.
                    status = SoftwareEncode(out size);
                }
            }
            else status = SoftwareEncode(out size);
            if (status != 0) throw new InvalidOperationException("HEIC: " + error);
            string backend = usedHardware != 0
                ? OperatingSystem.IsWindows() ? "Media Foundation hardware Main10" : "VideoToolbox hardware Main10"
                : "x265";
            Volatile.Write(ref _encoderDiagnostics, error.Length > 0 ? backend + "; fallback=" + error : backend);
            var file = new byte[checked((int)size)];
            Marshal.Copy(output, file, 0, file.Length);
            return file;

            int SoftwareEncode(out nuint length)
            {
                int result = Native.or_heif_encode_exif(pixels, frame.Pixels.Width, frame.Pixels.Height,
                    Math.Clamp(quality, 1, 100), hdr ? 1 : 0, profile, profile?.Length ?? 0,
                    exif, exif?.Length ?? 0, xmp, xmp?.Length ?? 0, out output, out length, error, error.Capacity);
                // The legacy ABI only writes the out buffer on failure.
                if (result == 0) error.Clear();
                return result;
            }
        }
        catch (DllNotFoundException ex)
        {
            throw new NotSupportedException("HEIC encoder is missing. Build packaging/heif/build.py for this platform.", ex);
        }
        finally
        {
            if (output != IntPtr.Zero) Native.or_heif_free(output);
            if (hardwareLease) Monitor.Exit(HardwareEncoderGate);
        }
    }

    private static class Native
    {
        private const string Library = "OpenRevelare.Heif.Native";
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int or_heif_encode_exif(ushort[] rgb, int width, int height, int quality,
            int hdr, byte[]? icc, int iccSize, byte[]? exif, int exifSize, byte[]? xmp, int xmpSize, out IntPtr output, out nuint outputSize,
            [Out] StringBuilder error, int errorSize);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int or_heif_encode_exif_accelerated(ushort[] rgb, int width, int height, int quality,
            int hdr, byte[]? icc, int iccSize, byte[]? exif, int exifSize, byte[]? xmp, int xmpSize,
            int allowHardware, out int hardwareUsed, out IntPtr output, out nuint outputSize,
            [Out] StringBuilder error, int errorSize);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void or_heif_free(IntPtr output);
    }
}
