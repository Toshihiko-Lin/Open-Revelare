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
}
