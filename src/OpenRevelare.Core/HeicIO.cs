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
            var (bytes, fit) = JpegIO.FitToSize(frame.Pixels.Width, frame.Pixels.Height, quality, ceiling, (edge, q) =>
            {
                if (Math.Max(scaled.Pixels.Width, scaled.Pixels.Height) > edge)
                    scaled = frame.WithPixels(Resample.ToLongEdge(frame.Pixels, edge, allowUpscale: false));
                return Encode(scaled, hdr, q, profile, sourceExif);
            }, formatName: "HEIC");
            ExportFile.Write(path, destination => File.WriteAllBytes(destination, bytes));
            return fit;
        }
        byte[] file = Encode(frame, hdr, quality, profile, sourceExif);
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
        float[] values = hdr ? HdrGainMap.HdrInBaseGamut(frame, ColorSpaces.Rec2020) : frame.Pixels.Data;
        var result = new ushort[values.Length];
        ParallelSweep.Over(values.Length, (from, to) =>
        {
            for (int i = from; i < to; i++)
            {
                float v = values[i];
                if (!float.IsFinite(v)) v = 0f;
                if (hdr) v = Pq.FromNits(v * OutputTarget.ReferenceWhiteNits);
                result[i] = (ushort)Math.Clamp((int)(Math.Clamp(v, 0f, 1f) * 1023f + 0.5f), 0, 1023);
            }
        });
        return result;
    }

    private static byte[] Encode(RenderedFrame frame, bool hdr, int quality, byte[]? profile, SourceExif? sourceExif)
    {
        ushort[] pixels = PreparePixels(frame);
        byte[]? exif = sourceExif?.ToByteArray(frame.Pixels.Width, frame.Pixels.Height,
            exactSrgb: !hdr && frame.OutputProfile.Identity == BuiltInColorProfiles.For(ColorSpaces.Srgb, ProfileRole.Output).Identity);
        byte[]? xmp = sourceExif?.MergeXmp(null);
        var error = new StringBuilder(1024);
        IntPtr output = IntPtr.Zero;
        try
        {
            int status = Native.or_heif_encode_exif(pixels, frame.Pixels.Width, frame.Pixels.Height,
                Math.Clamp(quality, 1, 100), hdr ? 1 : 0, profile, profile?.Length ?? 0,
                exif, exif?.Length ?? 0, xmp, xmp?.Length ?? 0, out output, out nuint size, error, error.Capacity);
            if (status != 0) throw new InvalidOperationException("HEIC: " + error);
            var file = new byte[checked((int)size)];
            Marshal.Copy(output, file, 0, file.Length);
            return file;
        }
        catch (DllNotFoundException ex)
        {
            throw new NotSupportedException("HEIC encoder is missing. Build packaging/heif/build.py for this platform.", ex);
        }
        finally { if (output != IntPtr.Zero) Native.or_heif_free(output); }
    }

    private static class Native
    {
        private const string Library = "OpenRevelare.Heif.Native";
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int or_heif_encode_exif(ushort[] rgb, int width, int height, int quality,
            int hdr, byte[]? icc, int iccSize, byte[]? exif, int exifSize, byte[]? xmp, int xmpSize, out IntPtr output, out nuint outputSize,
            [Out] StringBuilder error, int errorSize);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void or_heif_free(IntPtr output);
    }
}
