using System.Buffers.Binary;
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

        ReadOnlySpan<Half> rgba = finalOpaqueScene.Pixels;
        if (!finalOpaqueScene.IsKnownOpaque)
            RequireOpaque(rgba);

        byte[] bytes;
        ProfileIdentity? appliedProfile = null;
        int applicationTransformCount = 0;

        switch (contract.TransformOwner)
        {
            case FinalTransformOwner.SystemCompositor:
                bytes = PackCanonicalHalf(
                    rgba,
                    finalOpaqueScene.Size,
                    contract.ReferenceWhiteScale);
                break;

            case FinalTransformOwner.Application:
                ColorProfileRef deviceProfile = contract.DeviceProfile
                    ?? throw new PresentationContractException(
                        "Application-managed display contract has no exact device profile.");
                float[] scaledRgb = ExtractScaledRgb(rgba, contract.ReferenceWhiteScale);
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
                    rgba,
                    finalOpaqueScene.Size,
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

    private static void RequireOpaque(ReadOnlySpan<Half> rgba)
    {
        for (int i = 3; i < rgba.Length; i += 4)
        {
            if ((float)rgba[i] != 1f)
            {
                throw new ArgumentException(
                    $"Final presentation scene must be exactly opaque; pixel {i / 4} has alpha {(float)rgba[i]:R}.",
                    "finalOpaqueScene");
            }
        }
    }

    private static float[] ExtractScaledRgb(ReadOnlySpan<Half> rgba, float referenceWhiteScale)
    {
        var scaled = new float[checked((rgba.Length / 4) * 3)];
        int destination = 0;
        for (int source = 0; source < rgba.Length; source += 4)
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
        return scaled;
    }

    private static byte[] PackCanonicalHalf(
        ReadOnlySpan<Half> rgba,
        PixelSize size,
        float referenceWhiteScale)
    {
        var bytes = new byte[size.RequiredByteCount(bytesPerPixel: 8)];
        if (referenceWhiteScale == 1f && BitConverter.IsLittleEndian)
        {
            // RGBA-half already has the exact byte layout required by the system-managed
            // presentation surface. The scene is immutable and opacity was checked above, so a
            // single copy into the owned output byte array is sufficient.
            MemoryMarshal.AsBytes(rgba).CopyTo(bytes);
            return bytes;
        }

        Span<byte> output = bytes;
        for (int pixel = 0; pixel < size.PixelCount; pixel++)
        {
            int rgbaComponentOffset = pixel * 4;
            int rgbaOffset = pixel * 8;
            WriteHalf(
                output.Slice(rgbaOffset, 2),
                (float)rgba[rgbaComponentOffset] * referenceWhiteScale,
                pixel,
                channel: 0);
            WriteHalf(
                output.Slice(rgbaOffset + 2, 2),
                (float)rgba[rgbaComponentOffset + 1] * referenceWhiteScale,
                pixel,
                channel: 1);
            WriteHalf(
                output.Slice(rgbaOffset + 4, 2),
                (float)rgba[rgbaComponentOffset + 2] * referenceWhiteScale,
                pixel,
                channel: 2);
            BinaryPrimitives.WriteUInt16LittleEndian(
                output.Slice(rgbaOffset + 6, 2),
                BitConverter.HalfToUInt16Bits((Half)1f));
        }
        return bytes;

        static void WriteHalf(Span<byte> destination, float value, int pixel, int channel)
        {
            Half half = (Half)value;
            if (!float.IsFinite((float)half))
            {
                throw new InvalidOperationException(
                    $"Scaled canonical RGB at pixel {pixel}, channel {channel} exceeds finite half range.");
            }
            BinaryPrimitives.WriteUInt16LittleEndian(
                destination,
                BitConverter.HalfToUInt16Bits(half));
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
        for (int pixel = 0; pixel < size.PixelCount; pixel++)
        {
            int rgbOffset = pixel * 3;
            int bgraOffset = pixel * 4;
            bytes[bgraOffset] = QuantizeAndClipDevice(deviceRgb[rgbOffset + 2]);
            bytes[bgraOffset + 1] = QuantizeAndClipDevice(deviceRgb[rgbOffset + 1]);
            bytes[bgraOffset + 2] = QuantizeAndClipDevice(deviceRgb[rgbOffset]);
            bytes[bgraOffset + 3] = byte.MaxValue;
        }
        return bytes;
    }

    private static byte[] EncodeEmergencySrgb(
        ReadOnlySpan<Half> rgba,
        PixelSize size,
        float referenceWhiteScale)
    {
        var bytes = new byte[size.RequiredByteCount(bytesPerPixel: 4)];
        for (int pixel = 0; pixel < size.PixelCount; pixel++)
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
