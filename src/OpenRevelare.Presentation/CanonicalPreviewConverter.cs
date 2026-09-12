using OpenRevelare.ColorManagement;
using OpenRevelare.Core;

namespace OpenRevelare.Presentation;

/// <summary>
/// Converts one rendered, profile-described RGB frame into the platform-neutral canonical
/// preview encoding: D65 linear extended-sRGB in interleaved opaque RGBA-half order.
/// </summary>
public static class CanonicalPreviewConverter
{
    // Keep the two specified colorimetric steps visible. Both matrices come from the shared Core
    // implementation used to build/read the application's ICC profiles; neither stage clamps.
    private static readonly double[,] D50ToD65 =
        ColorSpaces.Adaptation(ColorSpaces.D50, ColorSpaces.Srgb.White);

    private static readonly double[,] D65XyzToLinearSrgb = ColorSpaces.Srgb.FromXyz();

    /// <summary>
    /// Decodes <paramref name="renderedFrame"/>'s exact output profile through the shared CMM,
    /// connects through D50 PCS XYZ, then performs the shared Bradford D50-to-D65 and D65 XYZ-to-
    /// linear-sRGB matrix steps. RGB is never clamped; values outside [0,1] survive into half.
    /// </summary>
    public static PresentationScene Convert(
        RenderedFrame renderedFrame,
        IColorManagementEngine colorManagement,
        float referenceWhiteScale)
    {
        ArgumentNullException.ThrowIfNull(renderedFrame);
        ArgumentNullException.ThrowIfNull(colorManagement);
        if (!float.IsFinite(referenceWhiteScale) || referenceWhiteScale <= 0f)
        {
            throw new ArgumentOutOfRangeException(
                nameof(referenceWhiteScale),
                "Reference-white scale must be finite and positive.");
        }

        var size = new PixelSize(renderedFrame.Pixels.Width, renderedFrame.Pixels.Height);

        // A scene-referred extended render already IS the canonical carrier: linear, Rec709
        // primaries, D65, unbounded. The conversion is the identity, so it is performed as one.
        //
        // THIS IS NOT AN OPTIMISATION, IT IS THE CORRECT TRANSFORM. Routing these pixels through
        // the CMM would ask an ICC engine to round-trip them via D50 PCS XYZ and back, and a PCS
        // is a bounded, display-referred connection space — the negatives and the values above
        // one that the extended render exists to carry are exactly what such a round trip is
        // entitled to discard. Doing nothing preserves them by construction.
        if (IsCanonicalCarrier(renderedFrame))
            return PresentationScene.FromOwnedPixels(ToOpaqueRgbaHalf(renderedFrame), size, referenceWhiteScale);

        float[] d50Xyz = new float[renderedFrame.Pixels.Data.Length];
        var request = new ColorTransformRequest(
            renderedFrame.OutputProfile,
            PcsColorProfiles.D50Xyz,
            TransformPurpose.CanonicalPreview,
            RenderingIntent.RelativeColorimetric,
            blackPointCompensation: false,
            adaptationState: 1.0,
            sourceFormat: PixelFormatDescriptor.RgbFloat32,
            destinationFormat: PixelFormatDescriptor.XyzFloat32);

        using (IColorTransformLease lease = colorManagement.Lease(request))
        {
            lease.Apply(renderedFrame.Pixels.Data, d50Xyz, size.PixelCount);
        }

        var rgba = new Half[checked(size.PixelCount * 4)];
        for (int pixel = 0; pixel < size.PixelCount; pixel++)
        {
            int rgbOffset = pixel * 3;
            double x50 = d50Xyz[rgbOffset];
            double y50 = d50Xyz[rgbOffset + 1];
            double z50 = d50Xyz[rgbOffset + 2];

            double x65 =
                D50ToD65[0, 0] * x50 + D50ToD65[0, 1] * y50 + D50ToD65[0, 2] * z50;
            double y65 =
                D50ToD65[1, 0] * x50 + D50ToD65[1, 1] * y50 + D50ToD65[1, 2] * z50;
            double z65 =
                D50ToD65[2, 0] * x50 + D50ToD65[2, 1] * y50 + D50ToD65[2, 2] * z50;

            int rgbaOffset = pixel * 4;
            rgba[rgbaOffset] = (Half)(
                D65XyzToLinearSrgb[0, 0] * x65 +
                D65XyzToLinearSrgb[0, 1] * y65 +
                D65XyzToLinearSrgb[0, 2] * z65);
            rgba[rgbaOffset + 1] = (Half)(
                D65XyzToLinearSrgb[1, 0] * x65 +
                D65XyzToLinearSrgb[1, 1] * y65 +
                D65XyzToLinearSrgb[1, 2] * z65);
            rgba[rgbaOffset + 2] = (Half)(
                D65XyzToLinearSrgb[2, 0] * x65 +
                D65XyzToLinearSrgb[2, 1] * y65 +
                D65XyzToLinearSrgb[2, 2] * z65);
            rgba[rgbaOffset + 3] = (Half)1f;
        }

        return PresentationScene.FromOwnedPixels(rgba, size, referenceWhiteScale);
    }

    /// <summary>
    /// True when the rendered pixels are already canonical presentation values — linear in the
    /// carrier's own primaries, tagged with the carrier's own profile.
    ///
    /// <para>
    /// Both halves are required. The transfer state alone would also admit scene-linear ACEScg
    /// (<c>OutputIntent.None</c>), whose primaries are emphatically not the carrier's, and the
    /// profile alone cannot be trusted to imply a linear encoding.
    /// </para>
    /// </summary>
    private static bool IsCanonicalCarrier(RenderedFrame frame) =>
        frame.Encoding.Transfer == TransferState.LinearInProfilePrimaries &&
        frame.OutputProfile.Identity ==
            BuiltInColorProfiles.LinearExtendedSrgb(ProfileRole.Output).Identity;

    /// <summary>
    /// Widens interleaved RGB float to premultiplied RGBA-half with alpha exactly one. The render
    /// boundary produces opaque pixels, so premultiplication is a no-op and the scene can be
    /// declared opaque on arrival.
    /// </summary>
    private static Half[] ToOpaqueRgbaHalf(RenderedFrame frame)
    {
        ReadOnlySpan<float> rgb = frame.Pixels.Data;
        int pixelCount = rgb.Length / 3;
        var rgba = new Half[checked(pixelCount * 4)];
        for (int pixel = 0; pixel < pixelCount; pixel++)
        {
            int source = pixel * 3;
            int destination = pixel * 4;
            rgba[destination] = (Half)rgb[source];
            rgba[destination + 1] = (Half)rgb[source + 1];
            rgba[destination + 2] = (Half)rgb[source + 2];
            rgba[destination + 3] = (Half)1f;
        }

        return rgba;
    }
}
