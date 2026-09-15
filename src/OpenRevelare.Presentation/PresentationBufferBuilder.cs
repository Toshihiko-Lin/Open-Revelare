using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Runtime.InteropServices;
using OpenRevelare.ColorManagement;
using OpenRevelare.Core;

namespace OpenRevelare.Presentation;

/// <summary>
/// Builds immutable bytes for one exact display contract from the final opaque canonical scene.
/// Reference-white scaling and the sole permitted 8-bit boundary clamp live here so platform
/// presenters cannot add hidden exposure or color transforms.
/// </summary>
public static class PresentationBufferBuilder
{
    public static PresentationBuffer Build(
        PresentationScene finalOpaqueScene,
        DisplayContract contract,
        IColorManagementEngine colorManagement)
        => Build(finalOpaqueScene, contract, colorManagement, reusableOutput: null);

    /// <summary>
    /// <see cref="Build(PresentationScene, DisplayContract, IColorManagementEngine)"/>, writing the
    /// packed bytes into <paramref name="reusableOutput"/> when it is exactly the required length
    /// (a fresh array is allocated otherwise, and returned via <see cref="PresentationBuffer.Bytes"/>
    /// for the caller to keep for next time).
    ///
    /// <para>
    /// The returned buffer ALIASES the array it was written into. The caller may hand the array
    /// back here only once nothing holds that buffer any more — for the presentation worker that
    /// is after the synchronous native present returns, since neither the mailbox nor the
    /// presenter retains a frame. A full-screen RGBA16F frame is 18 MB; allocating one per pointer
    /// move was most of the large-object churn behind the gen-2 stalls in a drag.
    /// </para>
    /// </summary>
    public static PresentationBuffer Build(
        PresentationScene finalOpaqueScene,
        DisplayContract contract,
        IColorManagementEngine colorManagement,
        byte[]? reusableOutput)
    {
        ArgumentNullException.ThrowIfNull(finalOpaqueScene);
        ArgumentNullException.ThrowIfNull(contract);
        ArgumentNullException.ThrowIfNull(colorManagement);

        if (finalOpaqueScene.ReferenceWhiteScale != contract.ReferenceWhiteScale)
        {
            throw new PresentationContractException(
                $"Canonical scene reference-white scale {finalOpaqueScene.ReferenceWhiteScale:R} " +
                $"does not exactly match display contract scale {contract.ReferenceWhiteScale:R}.");
        }

        if (!finalOpaqueScene.IsKnownOpaque)
            RequireOpaque(finalOpaqueScene);

        byte[] bytes;
        ProfileIdentity? appliedProfile = null;
        int applicationTransformCount = 0;

        switch (contract.TransformOwner)
        {
            case FinalTransformOwner.SystemCompositor:
                bytes = PackCanonicalHalf(
                    finalOpaqueScene,
                    contract.ReferenceWhiteScale,
                    reusableOutput);
                break;

            case FinalTransformOwner.Application:
                ColorProfileRef deviceProfile = contract.DeviceProfile
                    ?? throw new PresentationContractException(
                        "Application-managed display contract has no exact device profile.");
                float[] scaledRgb = ExtractScaledRgb(finalOpaqueScene, contract.ReferenceWhiteScale);
                bytes = TransformAndQuantizeMonitor(
                    scaledRgb,
                    finalOpaqueScene.Size,
                    deviceProfile,
                    colorManagement);
                appliedProfile = deviceProfile.Identity;
                applicationTransformCount = 1;
                break;

            case FinalTransformOwner.None:
                bytes = EncodeEmergencySrgb(
                    finalOpaqueScene,
                    contract.ReferenceWhiteScale);
                break;

            default:
                throw new ArgumentOutOfRangeException(
                    nameof(contract),
                    contract.TransformOwner,
                    "Unsupported final-transform owner.");
        }

        // No PresentationBuffer is published until every scale, transform, quantization and pack
        // step has completed. In particular, a CMM exception can expose only local scratch arrays.
        var result = PresentationBuffer.FromOwnedBytes(
            bytes,
            finalOpaqueScene.Size,
            contract.Encoding,
            contract.DisplayId,
            contract.Revision,
            appliedProfile,
            contract.SdrReferenceWhite,
            contract.ReferenceWhiteScale,
            applicationTransformCount);
        contract.Validate(result);
        return result;
    }

    // Every per-pixel loop below runs row-parallel through PresentationRows. The viewport is the
    // largest surface in the program — a 1900×1000 physical viewport is 1.9 M pixels, and it is
    // rebuilt on EVERY pointer move while a crop handle or the straighten line is being dragged,
    // and on every slider step. Serial, the scaled RGBA16F pack alone measured 55 ms on that
    // viewport, which on its own capped the drag at under 20 fps before a single pixel had been
    // composed. Each loop is pointwise, so row-splitting changes no value and no order of output.

    private static void RequireOpaque(PresentationScene scene)
    {
        ImmutableArray<Half> rgba = scene.LinearExtendedSrgbRgba;
        int width = scene.Size.Width;
        PresentationRows.Run(scene.Size.Height, width, y =>
        {
            int start = y * width * 4;
            int end = start + (width * 4);
            for (int i = start + 3; i < end; i += 4)
            {
                if ((float)rgba[i] != 1f)
                {
                    throw new ArgumentException(
                        $"Final presentation scene must be exactly opaque; pixel {i / 4} has alpha {(float)rgba[i]:R}.",
                        "finalOpaqueScene");
                }
            }
        });
    }

    private static float[] ExtractScaledRgb(PresentationScene scene, float referenceWhiteScale)
    {
        ImmutableArray<Half> rgba = scene.LinearExtendedSrgbRgba;
        int width = scene.Size.Width;
        var scaled = new float[checked(scene.Size.PixelCount * 3)];
        PresentationRows.Run(scene.Size.Height, width, y =>
        {
            int source = y * width * 4;
            int end = source + (width * 4);
            int destination = y * width * 3;
            for (; source < end; source += 4)
            {
                for (int channel = 0; channel < 3; channel++)
                {
                    float value = (float)rgba[source + channel] * referenceWhiteScale;
                    if (!float.IsFinite(value))
                    {
                        throw new InvalidOperationException(
                            $"Reference-white scaling produced a non-finite RGB component at pixel " +
                            $"{source / 4}, channel {channel}.");
                    }
                    scaled[destination++] = value;
                }
            }
        });
        return scaled;
    }

    private static byte[] PackCanonicalHalf(PresentationScene scene, float referenceWhiteScale, byte[]? reusable)
    {
        PixelSize size = scene.Size;
        int required = size.RequiredByteCount(bytesPerPixel: 8);
        byte[] bytes = reusable is not null && reusable.Length == required ? reusable : new byte[required];
        if (referenceWhiteScale == 1f && BitConverter.IsLittleEndian)
        {
            // RGBA-half already has the exact byte layout required by the system-managed
            // presentation surface. The scene is immutable and opacity was checked above, so a
            // single copy into the owned output byte array is sufficient.
            MemoryMarshal.AsBytes(scene.Pixels).CopyTo(bytes);
            return bytes;
        }

        // The scaled path is what an HDR display takes on every frame (D-020: the scale is
        // SdrWhiteNits/80, never 1 there). Row-parallel, writing the little-endian half bits
        // straight into the byte array; the output layout is exactly the one the serial loop
        // wrote, and every pixel's own finite check is kept.
        ImmutableArray<Half> rgba = scene.LinearExtendedSrgbRgba;
        int width = size.Width;
        ushort opaque = BitConverter.HalfToUInt16Bits((Half)1f);
        PresentationRows.Run(size.Height, width, y =>
        {
            ReadOnlySpan<Half> source = rgba.AsSpan().Slice(y * width * 4, width * 4);
            Span<ushort> row = MemoryMarshal.Cast<byte, ushort>(bytes.AsSpan(y * width * 8, width * 8));
            int firstPixel = y * width;
            for (int x = 0; x < width; x++)
            {
                int component = x * 4;
                row[component] = ScaleHalf(source[component], referenceWhiteScale, firstPixel + x, 0);
                row[component + 1] = ScaleHalf(source[component + 1], referenceWhiteScale, firstPixel + x, 1);
                row[component + 2] = ScaleHalf(source[component + 2], referenceWhiteScale, firstPixel + x, 2);
                row[component + 3] = opaque;
            }
        });
        if (!BitConverter.IsLittleEndian)
        {
            Span<ushort> all = MemoryMarshal.Cast<byte, ushort>(bytes);
            for (int i = 0; i < all.Length; i++)
                all[i] = BinaryPrimitives.ReverseEndianness(all[i]);
        }
        return bytes;

        static ushort ScaleHalf(Half value, float scale, int pixel, int channel)
        {
            Half half = (Half)((float)value * scale);
            if (!float.IsFinite((float)half))
            {
                throw new InvalidOperationException(
                    $"Scaled canonical RGB at pixel {pixel}, channel {channel} exceeds finite half range.");
            }
            return BitConverter.HalfToUInt16Bits(half);
        }
    }

    private static byte[] TransformAndQuantizeMonitor(
        float[] scaledRgb,
        PixelSize size,
        ColorProfileRef deviceProfile,
        IColorManagementEngine colorManagement)
    {
        var request = new ColorTransformRequest(
            BuiltInColorProfiles.LinearExtendedSrgb(ProfileRole.CanonicalPresentation),
            deviceProfile,
            TransformPurpose.MonitorPresentation,
            RenderingIntent.RelativeColorimetric,
            blackPointCompensation: true,
            adaptationState: 1.0,
            sourceFormat: PixelFormatDescriptor.RgbFloat32,
            destinationFormat: PixelFormatDescriptor.RgbFloat32);
        var deviceRgb = new float[scaledRgb.Length];
        using (IColorTransformLease transform = colorManagement.Lease(request))
        {
            transform.Apply(scaledRgb, deviceRgb, size.PixelCount);
        }

        return PackMonitorBgra8(deviceRgb, size);
    }

    private static byte[] PackMonitorBgra8(float[] deviceRgb, PixelSize size)
    {
        var bytes = new byte[size.RequiredByteCount(bytesPerPixel: 4)];
        int width = size.Width;
        PresentationRows.Run(size.Height, width, y =>
        {
            int pixel = y * width;
            int end = pixel + width;
            for (; pixel < end; pixel++)
            {
                int rgbOffset = pixel * 3;
                int bgraOffset = pixel * 4;
                bytes[bgraOffset] = QuantizeAndClipDevice(deviceRgb[rgbOffset + 2]);
                bytes[bgraOffset + 1] = QuantizeAndClipDevice(deviceRgb[rgbOffset + 1]);
                bytes[bgraOffset + 2] = QuantizeAndClipDevice(deviceRgb[rgbOffset]);
                bytes[bgraOffset + 3] = byte.MaxValue;
            }
        });
        return bytes;
    }

    private static byte[] EncodeEmergencySrgb(PresentationScene scene, float referenceWhiteScale)
    {
        PixelSize size = scene.Size;
        ImmutableArray<Half> rgba = scene.LinearExtendedSrgbRgba;
        var bytes = new byte[size.RequiredByteCount(bytesPerPixel: 4)];
        int width = size.Width;
        PresentationRows.Run(size.Height, width, y =>
        {
            int pixel = y * width;
            int end = pixel + width;
            for (; pixel < end; pixel++)
            {
                int rgbaOffset = pixel * 4;
                int bgraOffset = pixel * 4;
                bytes[bgraOffset] = EncodeLinearSrgbAndQuantize(
                    (float)rgba[rgbaOffset + 2] * referenceWhiteScale);
                bytes[bgraOffset + 1] = EncodeLinearSrgbAndQuantize(
                    (float)rgba[rgbaOffset + 1] * referenceWhiteScale);
                bytes[bgraOffset + 2] = EncodeLinearSrgbAndQuantize(
                    (float)rgba[rgbaOffset] * referenceWhiteScale);
                bytes[bgraOffset + 3] = byte.MaxValue;
            }
        });
        return bytes;
    }

    /// <summary>The one explicit device-encoded float to byte clip/quantization boundary.</summary>
    private static byte QuantizeAndClipDevice(float value)
    {
        if (!float.IsFinite(value))
            throw new InvalidOperationException("Monitor transform produced a non-finite RGB component.");
        if (value <= 0f) return 0;
        if (value >= 1f) return byte.MaxValue;
        return (byte)((value * byte.MaxValue) + 0.5f);
    }

    /// <summary>
    /// Emergency output has no monitor transform, but it is still explicitly encoded as sRGB.
    /// The comparisons are its sole final range resolution; in-range values then receive the
    /// IEC 61966-2-1 forward TRC and one byte quantization.
    /// </summary>
    private static byte EncodeLinearSrgbAndQuantize(float linear)
    {
        if (!float.IsFinite(linear))
            throw new InvalidOperationException("Emergency presentation received a non-finite RGB component.");
        if (linear <= 0f) return 0;
        if (linear >= 1f) return byte.MaxValue;

        float encoded = linear <= 0.0031308f
            ? linear * 12.92f
            : (1.055f * MathF.Pow(linear, 1f / 2.4f)) - 0.055f;
        return (byte)((encoded * byte.MaxValue) + 0.5f);
    }
}
