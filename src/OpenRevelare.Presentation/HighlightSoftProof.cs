namespace OpenRevelare.Presentation;

/// <summary>
/// Fits a scene rendered for one highlight headroom into the smaller headroom of the display
/// under the window, instead of letting the compositor clip it (D-028).
///
/// <para>
/// WHY THIS IS PRESENTATION AND NOT RENDERING. An extended render aims its shoulder at the
/// roll's target (<c>peak / 203</c> times diffuse white); the panel shows up to
/// <c>panelPeak / sdrWhite</c> times it. Between the two, the compositor hard-clips — a 1000-nit
/// master on a 520-nit panel loses its top 0.2 stops to a flat white, which is neither the
/// picture nor what the panel is capable of. Lightroom compresses those highlights into the
/// available range and so does this: the same monotone shoulder, aimed at the display. It runs
/// in presentation generation, on the display's own headroom, which is exactly the dependency
/// I5 permits there and forbids in <c>RenderedFrame</c> and exports — nothing here feeds either.
/// </para>
///
/// <para>
/// THE SDR RANGE IS UNTOUCHED. The knee is exactly diffuse white (canonical 1.0): everything a
/// print could show is presented as rendered, so the soft proof cannot re-grade the mid-tones or
/// move paper white. Only the part above it — the part that exists because of the HDR target —
/// is reshaped, from <c>(1, content]</c> onto <c>(1, display]</c>, C¹ at the knee (slope 1) and
/// hitting the display's ceiling exactly at the content's, so the brightest rendered value lands
/// on the brightest the panel has rather than short of it.
/// </para>
///
/// <para>
/// WHEN IT DOES NOTHING. A display with headroom at or above the content's shows the render as
/// is. A display with no headroom at all (SDR mode, or an HDR panel with the SDR slider at its
/// peak) also gets the render as is — and clips. Mapping <c>(1, content]</c> onto nothing would
/// have to move the knee below 1 to show anything, which re-grades the SDR range; the honest
/// answer there is the clip, said out loud by the status badge, and the SDR tier for the roll.
/// </para>
///
/// <para>
/// THE SHOULDER IS APPLIED PER PIXEL, NOT PER CHANNEL (D-036). It is driven by the pixel's
/// largest component, and the whole colour is scaled by the one factor that takes that
/// component to where the shoulder puts it, so hue and saturation are carried across unchanged
/// and only the luminance is fitted. Compressing each channel on its own bent the ratios of every
/// highlight that reached above the knee — a warm highlight shed its red faster than its blue —
/// which showed as a cast in the highlights whenever exposure pushed more of the picture up
/// there. "The SDR range is untouched" therefore holds per pixel: a colour whose largest
/// component is at or below the knee is exactly as rendered.
/// </para>
/// </summary>
public static class HighlightSoftProof
{
    /// <summary>Diffuse white in the canonical carrier; the knee, below which nothing changes.</summary>
    public const float Knee = 1f;

    /// <summary>
    /// True when a scene rendered for <paramref name="contentHeadroom"/> would clip on a display
    /// with <paramref name="displayHeadroom"/> and there is some headroom to fit it into.
    /// </summary>
    public static bool IsNeeded(float contentHeadroom, float displayHeadroom) =>
        float.IsFinite(contentHeadroom) && float.IsFinite(displayHeadroom) &&
        contentHeadroom > Knee && displayHeadroom > Knee && displayHeadroom < contentHeadroom;

    /// <summary>
    /// One sample. Identity at and below the knee; above it a rational shoulder that leaves the
    /// knee with slope 1 and reaches <paramref name="displayHeadroom"/> exactly at
    /// <paramref name="contentHeadroom"/>.
    /// </summary>
    public static float Of(float value, float contentHeadroom, float displayHeadroom)
    {
        if (!(value > Knee)) return value;   // NaN and negatives take this exit too

        float contentSpan = contentHeadroom - Knee;
        float displaySpan = displayHeadroom - Knee;
        // x ∈ [0,1]: how far up the content's headroom this sample sits. Input is bounded by the
        // render (BoundAbove), but a half-precision round-up past the ceiling must not overshoot.
        float x = (value - Knee) / contentSpan;
        if (x > 1f) x = 1f;
        // g(x) = kx / (1 + (k−1)x): g(0)=0, g(1)=1, g'(0)=k. With k = contentSpan / displaySpan
        // the composite has slope exactly 1 at the knee, so the curve continues the identity
        // without a crease, and is concave — brighter stays brighter, nothing crosses.
        float k = contentSpan / displaySpan;
        float g = k * x / (1f + (k - 1f) * x);
        return Knee + displaySpan * g;
    }

    /// <summary>
    /// The scene fitted to <paramref name="displayHeadroom"/>, or the scene itself when nothing
    /// needs fitting. Colour channels only; alpha is carried across. Meant for the frame scene,
    /// which is opaque — on a premultiplied translucent pixel the shoulder would act on the
    /// premultiplied value, which is not the colour's own headroom.
    /// </summary>
    public static PresentationScene Fit(PresentationScene scene, float contentHeadroom, float displayHeadroom)
    {
        ArgumentNullException.ThrowIfNull(scene);
        if (!IsNeeded(contentHeadroom, displayHeadroom)) return scene;

        ReadOnlySpan<Half> source = scene.Pixels;
        var fitted = new Half[source.Length];
        for (int i = 0; i < source.Length; i += 4)
        {
            float r = (float)source[i], g = (float)source[i + 1], b = (float)source[i + 2];
            float max = MathF.Max(r, MathF.Max(g, b));
            if (max > Knee)
            {
                float scale = Of(max, contentHeadroom, displayHeadroom) / max;
                r *= scale; g *= scale; b *= scale;
            }
            fitted[i] = (Half)r;
            fitted[i + 1] = (Half)g;
            fitted[i + 2] = (Half)b;
            fitted[i + 3] = source[i + 3];
        }
        return PresentationScene.FromOwnedPixels(fitted, scene.Size, scene.ReferenceWhiteScale);
    }
}
