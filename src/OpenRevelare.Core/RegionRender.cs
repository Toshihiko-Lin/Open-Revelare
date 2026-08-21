namespace OpenRevelare.Core;

/// <summary>
/// Renders ONE rectangle of a frame at full source resolution — the sharp patch behind
/// pixel-peeping zoom, ported from the Python GUI's ROI path (main_window.py::_HiresWorker +
/// preview_widget's roi blit).
///
/// The preview the GUI normally shows is box-downsampled 6× on a 60 MP frame, so zooming into
/// it magnifies preview pixels and invents no detail. This runs the same pipeline over just the
/// visible slice of the ORIGINAL pixels, so focus, grain and sharpness are real.
///
/// WHY THIS IS NOT "crop the source and call ProcessFrame":
/// four operators in the chain are frame-global, and handed a bare slice each would silently
/// re-centre on it — vignette and distortion are radial about the frame centre, the straighten
/// rotation pivots about it, and the LCC flat field describes the whole frame. They take a
/// <see cref="FrameRegion"/> for exactly this. The remaining geometry (orientation → rotation →
/// crop) is not applied as three passes either: it is composed into ONE inverse map from output
/// pixel to source coordinate, which is both less code and structurally identical to the chain
/// it replaces — one bilinear sample, same taps, same weights.
///
/// NO INTERMEDIATE DOWNSAMPLE (unlike the Python worker, which shrinks the crop to the viewport
/// first). Doing that means running the geometry in a scaled coordinate system, and the
/// half-pixel bookkeeping of "box-downsample by f, then rotate about a centre divided by f" is
/// precisely where this kind of code drifts by a fraction of a pixel against the fit view. Work
/// is bounded instead by <see cref="MaxSourcePixels"/>: the caller only asks for a patch once
/// the visible area is small enough to be cheap, which at normal viewing sizes is the same
/// moment the patch starts being worth rendering. The cost is that between fit and that
/// threshold we keep showing the ordinary preview.
/// </summary>
public static class RegionRender
{
    /// <summary>Normalised rectangle in DISPLAYED (post-geometry) coordinates.</summary>
    public readonly record struct Roi(double X, double Y, double W, double H);

    /// <summary>
    /// Ceiling on source pixels one patch may touch, and therefore also the thing that decides
    /// WHEN a patch is worth asking for: on a 60 MP frame in a 1240x900 viewport it starts
    /// engaging around 3.5x fit (~274% on screen), i.e. once the user is genuinely pixel-peeping.
    /// Below that the ordinary preview keeps standing in.
    ///
    /// Measured on a 12-core desktop: 3.3 MP of source takes ~215 ms, so this ceiling is roughly
    /// half a second — the worst case, not the common one. A typical engaged patch is 1-3 MP.
    /// </summary>
    public const long MaxSourcePixels = 8_000_000;

    /// <summary>Dimensions <see cref="Pipeline.ProcessFrame"/> would output for this frame —
    /// i.e. the coordinate system <see cref="Roi"/> is expressed in.</summary>
    public static (int W, int H) DisplayedSize(int srcW, int srcH, FrameParams cal)
    {
        int w = srcW, h = srcH;
        if ((((cal.QuarterTurns % 4) + 4) % 4) % 2 == 1) (w, h) = (h, w);
        if (cal.CropRect is { } r)
        {
            int x0 = Math.Max(0, (int)Math.Round(r.X * w));
            int y0 = Math.Max(0, (int)Math.Round(r.Y * h));
            int x1 = Math.Min(w, (int)Math.Round((r.X + r.W) * w));
            int y1 = Math.Min(h, (int)Math.Round((r.Y + r.H) * h));
            w = Math.Max(1, x1 - x0);
            h = Math.Max(1, y1 - y0);
        }
        return (w, h);
    }

    /// <summary>
    /// Source pixels a patch would have to touch — the caller's budget check.
    ///
    /// Takes DIMENSIONS, not the buffer, on purpose: the whole point is to answer "is this worth
    /// doing?" before anyone pays for the full-resolution decode that <see cref="Render"/> needs.
    /// The preview cache already knows the source size, so the question is free.
    /// </summary>
    public static long SourcePixelsFor(int srcW, int srcH, FrameParams cal, Roi roi)
    {
        var b = RequiredSourceBounds(srcW, srcH, cal, roi);
        return (long)(b.X1 - b.X0) * (b.Y1 - b.Y0);
    }

    /// <summary>
    /// The source rectangle a patch needs, so a caller can decode ONLY that instead of the whole
    /// frame. Half-open: [X0,X1) × [Y0,Y1).
    /// </summary>
    public static (int X0, int Y0, int X1, int Y1) RequiredSourceBounds(
        int srcW, int srcH, FrameParams cal, Roi roi)
    {
        var (rect, _) = Realise(srcW, srcH, cal, roi);
        return SourceBounds(srcW, srcH, cal, rect);
    }

    /// <summary>
    /// <see cref="RequiredSourceBounds"/> for the NEGATIVE patch: the ROI un-oriented onto the raw
    /// frame, because that view applies ORIENTATION ONLY — no straighten, no crop (see
    /// <see cref="RenderNegative"/>). Callers that decode a region must use this one when asking
    /// for a negative, or they will reserve the rectangle the fully-geometried frame would need
    /// and hand back the wrong pixels.
    /// </summary>
    public static (int X0, int Y0, int X1, int Y1) RequiredSourceBoundsNegative(
        int srcW, int srcH, FrameParams cal, Roi roi)
    {
        Roi raw = UnorientRoi(roi, cal);
        int x0 = Math.Clamp((int)Math.Floor(raw.X * srcW), 0, srcW - 1);
        int y0 = Math.Clamp((int)Math.Floor(raw.Y * srcH), 0, srcH - 1);
        int x1 = Math.Clamp((int)Math.Ceiling((raw.X + raw.W) * srcW), x0 + 1, srcW);
        int y1 = Math.Clamp((int)Math.Ceiling((raw.Y + raw.H) * srcH), y0 + 1, srcH);
        return (x0, y0, x1, y1);
    }

    /// <summary>
    /// Render the patch. Returns the image plus the REALISED rectangle — the request is rounded
    /// to whole displayed pixels, and the caller must blit against what was actually produced,
    /// not what it asked for, or the patch lands fractionally off and shimmers against the
    /// preview underneath.
    /// </summary>
    public static (ImageBuffer Image, Roi Realised) Render(ImageBuffer full, FrameParams cal, Roi roi,
                                                          bool negative = false,
                                                          double[]? negativeWb = null)
    {
        // The negative path reads the frame in its own coordinates, so hand it the whole buffer
        // rather than the geometry-derived slice RequiredSourceBounds computes for the positive.
        if (negative) return RenderNegative(full, 0, 0, full.Width, full.Height, cal, roi, negativeWb);

        var b = RequiredSourceBounds(full.Width, full.Height, cal, roi);
        int sw = b.X1 - b.X0, sh = b.Y1 - b.Y0;
        var slice = new ImageBuffer(sw, sh);
        for (int y = 0; y < sh; y++)
            Array.Copy(full.Data, ((b.Y0 + y) * full.Width + b.X0) * 3, slice.Data, y * sw * 3, sw * 3);
        return RenderFromSlice(slice, b.X0, b.Y0, full.Width, full.Height, cal, roi, negative, negativeWb);
    }

    /// <summary>
    /// <inheritdoc cref="Render"/>
    /// </summary>
    /// <param name="source">A slice of the frame that CONTAINS
    /// <see cref="RequiredSourceBounds"/> — it may be larger (a decode with pan margin) but must
    /// not be smaller, or the patch degrades at the border where the operators clamp.</param>
    /// <param name="sourceX0">Slice origin in frame coordinates.</param>
    /// <param name="sourceY0">Slice origin in frame coordinates.</param>
    /// <param name="frameW">Full frame width — what the frame-global operators measure against.</param>
    /// <param name="frameH">Full frame height.</param>
    /// <param name="negative">
    /// Render the UN-INVERTED negative instead of the finished positive — what the film-base
    /// sampling view shows. Hands off to <see cref="RenderNegative"/>, which applies the step-4
    /// conversion and nothing else; see there for why not even geometry runs.
    /// </param>
    /// <param name="negativeWb">
    /// Green-normalised display white balance for the negative path (<see cref="RawDecode.CameraWhiteBalance"/>),
    /// or null to show the bare UniWB decode. Ignored unless <paramref name="negative"/> is set —
    /// the positive path white-balances in Stage 2, from the user's own temp/tint.
    /// </param>
    public static (ImageBuffer Image, Roi Realised) RenderFromSlice(
        ImageBuffer source, int sourceX0, int sourceY0, int frameW, int frameH,
        FrameParams cal, Roi roi, bool negative = false, double[]? negativeWb = null)
    {
        if (negative)
            return RenderNegative(source, sourceX0, sourceY0, frameW, frameH, cal, roi, negativeWb);

        var (rect, realised) = Realise(frameW, frameH, cal, roi);
        var b = SourceBounds(frameW, frameH, cal, rect);

        // Narrow the caller's slice to exactly what is needed. Costs one copy and keeps every
        // operator below working on the tight rectangle regardless of how generous the decode was.
        int sw = b.X1 - b.X0, sh = b.Y1 - b.Y0;
        var slice = new ImageBuffer(sw, sh);
        for (int y = 0; y < sh; y++)
        {
            int sy = b.Y0 + y - sourceY0;
            int sx = b.X0 - sourceX0;
            Array.Copy(source.Data, (sy * source.Width + sx) * 3, slice.Data, y * sw * 3, sw * 3);
        }

        var region = new FrameRegion(b.X0, b.Y0, frameW, frameH);
        if (cal.DistortionK1 != 0.0)
        {
            // Distortion RESAMPLES, so its output slice is the sub-rect we actually want while
            // its input is the wider box SourceBounds reserved. Both live at the same offset.
            slice = LensCorrections.ApplyDistortion(slice, cal.DistortionK1, region);
        }
        if (cal.LccFlatField != null)
            Lcc.Apply(slice.Data, sw, sh, cal.LccFlatField, region);
        if (cal.VignetteAmount != 0.0)
            LensCorrections.ApplyVignette(slice.Data, sw, sh, cal.VignetteAmount, cal.VignetteFalloff, region);

        bool[]? mask = null;
        if (cal.SprocketEnabled && cal.SprocketThreshold is double thr)
            mask = Sprocket.MakeMask(slice.Data, slice.PixelCount, (float)thr);

        // Mirrors Pipeline.ProcessFrame — the preview must predict the export.
        if (InputTransform.ToWorking(cal.InputPrimaries, cal.InputWhitePoint) is double[,] inputM)
            InputTransform.Apply(slice.Data, inputM);

        if (cal.DecoupleMatrix != null)
            Decouple.Apply(slice.Data, cal.DecoupleMatrix, cal.DecoupleMode);

        ImageBuffer inverted = Inversion.Invert(slice, cal, cal.DecoupleChromaAmp,
                                                Pipeline.ResolveChromaMatrix(cal));
        if (mask != null) Sprocket.ApplyMask(inverted.Data, mask);

        // ── Geometry: one composed inverse map, output rect → source coordinate ──
        ImageBuffer outImg = MapGeometry(inverted, b.X0, b.Y0, frameW, frameH, cal, rect);

        // ── Step 4 + Stage 2 (pointwise; no region dependence) ───────────────────
        if (cal.OutputIntent == OutputIntent.Basic)
            Stage2.ApplyChain(outImg.Data, cal, cal.ResolvedOutputSpace, encodeExit: true);
        return (outImg, realised);
    }

    /// <summary>
    /// A displayed-space (oriented) ROI expressed against the RAW frame's axes.
    ///
    /// The negative view applies orientation and nothing else, so this is the whole of the inverse
    /// map: undo the flips, then the quarter turns — the reverse of the forward order, which runs
    /// turns first and flips after. A quarter turn also swaps which normalised axis is which,
    /// hence the W/H exchange on each step.
    /// </summary>
    public static Roi UnorientRoi(Roi roi, FrameParams cal)
    {
        if (cal.FlipV) roi = roi with { Y = 1.0 - roi.Y - roi.H };
        if (cal.FlipH) roi = roi with { X = 1.0 - roi.X - roi.W };
        for (int i = 0; i < (((cal.QuarterTurns % 4) + 4) % 4); i++)
            roi = new Roi(roi.Y, 1.0 - roi.X - roi.W, roi.H, roi.W);   // undo one CW turn
        return roi;
    }

    /// <summary>The inverse of <see cref="UnorientRoi"/>: a raw-frame ROI forward into the
    /// displayed (oriented) space the caller blits against. One CW turn maps (u,v) → (1-v, u),
    /// matching <see cref="Geometry.ApplyOrientation"/>'s pixel remap.</summary>
    private static Roi OrientRoi(Roi roi, FrameParams cal)
    {
        for (int i = 0; i < (((cal.QuarterTurns % 4) + 4) % 4); i++)
            roi = new Roi(1.0 - roi.Y - roi.H, roi.X, roi.H, roi.W);   // one turn CW
        if (cal.FlipH) roi = roi with { X = 1.0 - roi.X - roi.W };
        if (cal.FlipV) roi = roi with { Y = 1.0 - roi.Y - roi.H };
        return roi;
    }

    /// <summary>
    /// The patch for the FILM-BASE SAMPLING view: a slice of the raw negative, oriented to match
    /// the view, converted to the output space, and nothing more.
    ///
    /// Deliberately shares none of the positive path's machinery, because it has to match a view
    /// that shares none of the pipeline. <c>ShowNegativeView</c> takes the PREVIEW BUFFER — the
    /// bare decode — turns it by the frame's orientation and runs it through
    /// <see cref="ColorPipeline.ToOutputSpace"/>. Everything else (distortion, LCC, vignette,
    /// input transform, decouple, inversion, straighten, crop, Stage 2) lives inside
    /// <see cref="Pipeline.ProcessFrame"/> and has therefore not run.
    ///
    /// ORIENTATION is the one piece of geometry that DOES apply, so the ROI arrives in displayed
    /// coordinates like every other patch and is mapped back with <see cref="UnorientRoi"/> to
    /// find the raw pixels. It cannot go through <see cref="Realise"/>, though: that also applies
    /// the straighten rotation and the crop, neither of which the picture underneath has been
    /// through.
    /// </summary>
    private static (ImageBuffer Image, Roi Realised) RenderNegative(
        ImageBuffer source, int sourceX0, int sourceY0, int frameW, int frameH,
        FrameParams cal, Roi roi, double[]? negativeWb = null)
    {
        Roi raw = UnorientRoi(roi, cal);
        int x0 = Math.Clamp((int)Math.Floor(raw.X * frameW), 0, frameW - 1);
        int y0 = Math.Clamp((int)Math.Floor(raw.Y * frameH), 0, frameH - 1);
        int x1 = Math.Clamp((int)Math.Ceiling((raw.X + raw.W) * frameW), x0 + 1, frameW);
        int y1 = Math.Clamp((int)Math.Ceiling((raw.Y + raw.H) * frameH), y0 + 1, frameH);

        // Clamp to what the caller's slice actually covers — a region decode is only guaranteed
        // to hold the bounds that were asked for, and reading past it would walk off the buffer.
        x0 = Math.Max(x0, sourceX0);
        y0 = Math.Max(y0, sourceY0);
        x1 = Math.Min(x1, sourceX0 + source.Width);
        y1 = Math.Min(y1, sourceY0 + source.Height);
        if (x1 <= x0 || y1 <= y0) return (new ImageBuffer(1, 1), new Roi(0, 0, 1, 1));

        int w = x1 - x0, h = y1 - y0;
        var outImg = new ImageBuffer(w, h);
        for (int y = 0; y < h; y++)
            Array.Copy(source.Data, ((y0 + y - sourceY0) * source.Width + (x0 - sourceX0)) * 3,
                       outImg.Data, y * w * 3, w * 3);

        // Turn the patch the same way the view was turned, and report the realised rectangle in
        // the displayed space the caller blits into. Rounding to whole RAW pixels above and
        // mapping the result forward keeps the two consistent — reporting the requested ROI
        // instead would land the patch fractionally off and shimmer against the preview.
        outImg = Geometry.ApplyOrientation(outImg, cal.QuarterTurns, cal.FlipH, cal.FlipV);
        // The camera's own white balance, for VIEWING ONLY — same gains, same place in the chain
        // (scene-linear, before the encode) as ShowNegativeView. Without it a UniWB negative reads
        // green and the orange film base this view exists to point at does not look orange. The
        // samplers are unaffected: they read the UniWB preview buffer, never these pixels.
        NegativeView.ApplyWhiteBalance(outImg.Data, negativeWb);
        // PLAIN step 4 — never the roll's print-film emulation, even when one is selected. This
        // patch is un-inverted film, and a print stock characterises how a finished POSITIVE
        // prints; running the negative through it renders a look over the very pixels the user
        // opened this view to sample. It must also match ShowNegativeView, which composes the
        // whole-frame version of this picture the same plain way — the patch and the preview
        // underneath it are the same image at two resolutions, so a difference here shows up as
        // the patch flashing a different colour wherever the user zooms.
        ColorPipeline.ToOutputSpace(outImg.Data, cal.ResolvedOutputSpace);
        return (outImg, OrientRoi(new Roi((double)x0 / frameW, (double)y0 / frameH,
                                          (double)w / frameW, (double)h / frameH), cal));
    }

    // ── request → whole displayed pixels ─────────────────────────────────────────
    private static ((int X0, int Y0, int X1, int Y1) Rect, Roi Realised) Realise(
        int srcW, int srcH, FrameParams cal, Roi roi)
    {
        var (dw, dh) = DisplayedSize(srcW, srcH, cal);
        int x0 = Math.Clamp((int)Math.Floor(roi.X * dw), 0, dw - 1);
        int y0 = Math.Clamp((int)Math.Floor(roi.Y * dh), 0, dh - 1);
        int x1 = Math.Clamp((int)Math.Ceiling((roi.X + roi.W) * dw), x0 + 1, dw);
        int y1 = Math.Clamp((int)Math.Ceiling((roi.Y + roi.H) * dh), y0 + 1, dh);
        return ((x0, y0, x1, y1), new Roi((double)x0 / dw, (double)y0 / dh,
                                          (double)(x1 - x0) / dw, (double)(y1 - y0) / dh));
    }

    // ── which source pixels that output rect needs ───────────────────────────────
    private static (int X0, int Y0, int X1, int Y1) SourceBounds(
        int srcW, int srcH, FrameParams cal, (int X0, int Y0, int X1, int Y1) rect)
    {
        var (ow, oh) = OrientedSize(srcW, srcH, cal);
        var (cx0, cy0) = CropOrigin(ow, oh, cal);

        // Output rect → oriented space. Rotation is affine, so its four corners bound it.
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        foreach (var (px, py) in new[] { (rect.X0, rect.Y0), (rect.X1, rect.Y0),
                                         (rect.X0, rect.Y1), (rect.X1, rect.Y1) })
        {
            var (ox, oy) = RotatedToOriented(px + cx0, py + cy0, ow, oh, cal.Rotation);
            if (ox < minX) minX = ox; if (ox > maxX) maxX = ox;
            if (oy < minY) minY = oy; if (oy > maxY) maxY = oy;
        }
        // Two pixels of slack: one for the bilinear tap, one for floor/ceil.
        minX -= 2; minY -= 2; maxX += 2; maxY += 2;

        // Oriented → source: orientation is a permutation, so a box maps to a box.
        double sx0 = double.MaxValue, sy0 = double.MaxValue, sx1 = double.MinValue, sy1 = double.MinValue;
        foreach (var (ox, oy) in new[] { (minX, minY), (maxX, minY), (minX, maxY), (maxX, maxY) })
        {
            var (px, py) = OrientedToSource(ox, oy, srcW, srcH, cal);
            if (px < sx0) sx0 = px; if (px > sx1) sx1 = px;
            if (py < sy0) sy0 = py; if (py > sy1) sy1 = py;
        }

        // Distortion pulls its samples in from further out; ask it how far.
        if (cal.DistortionK1 != 0.0)
        {
            var d = LensCorrections.DistortionSourceBounds(sx0, sy0, sx1, sy1,
                                                           cal.DistortionK1, srcW, srcH);
            sx0 = Math.Min(sx0, d.X0); sy0 = Math.Min(sy0, d.Y0);
            sx1 = Math.Max(sx1, d.X1); sy1 = Math.Max(sy1, d.Y1);
            sx0 -= 2; sy0 -= 2; sx1 += 2; sy1 += 2;
        }

        int ix0 = Math.Clamp((int)Math.Floor(sx0), 0, srcW - 1);
        int iy0 = Math.Clamp((int)Math.Floor(sy0), 0, srcH - 1);
        int ix1 = Math.Clamp((int)Math.Ceiling(sx1) + 1, ix0 + 1, srcW);
        int iy1 = Math.Clamp((int)Math.Ceiling(sy1) + 1, iy0 + 1, srcH);
        return (ix0, iy0, ix1, iy1);
    }

    // ── the composed geometry map ────────────────────────────────────────────────
    /// <summary>
    /// Produce the output rect by sampling <paramref name="src"/> (a processed source slice at
    /// <paramref name="ofsX"/>,<paramref name="ofsY"/>) through orientation → rotation → crop,
    /// composed. Reproduces <see cref="Geometry.ApplyRotation"/>'s arithmetic exactly, including
    /// its scipy-derived edge rules, so the patch matches the full-frame render tap for tap.
    /// </summary>
    private static ImageBuffer MapGeometry(ImageBuffer src, int ofsX, int ofsY, int srcW, int srcH,
                                           FrameParams cal, (int X0, int Y0, int X1, int Y1) rect)
    {
        var (ow, oh) = OrientedSize(srcW, srcH, cal);
        var (cx0, cy0) = CropOrigin(ow, oh, cal);
        int outW = rect.X1 - rect.X0, outH = rect.Y1 - rect.Y0;
        var dst = new ImageBuffer(outW, outH);
        float[] s = src.Data, d = dst.Data;
        int sw = src.Width, sh = src.Height;
        const float Fill = 1.0f;   // Geometry.ApplyRotation's white corners

        Parallel.For(0, outH, oy =>
        {
            for (int ox = 0; ox < outW; ox++)
            {
                int di = (oy * outW + ox) * 3;
                // displayed → rotated
                double rx = rect.X0 + ox + cx0, ry = rect.Y0 + oy + cy0;
                // rotated → oriented (the rotation's own output→input map)
                var (xi, yi) = RotatedToOriented(rx, ry, ow, oh, cal.Rotation);

                if (cal.Rotation != 0.0 && (xi < 0.0 || xi > ow - 1 || yi < 0.0 || yi > oh - 1))
                {
                    d[di] = Fill; d[di + 1] = Fill; d[di + 2] = Fill;
                    continue;
                }

                // Bilinear in ORIENTED space, matching ApplyRotation's stepback rule.
                int x0 = (int)Math.Floor(xi), y0 = (int)Math.Floor(yi);
                double fx = xi - x0, fy = yi - y0;
                if (cal.Rotation != 0.0)
                {
                    if (x0 >= ow - 1) { x0 = ow - 2; fx = 1.0; }
                    if (y0 >= oh - 1) { y0 = oh - 2; fy = 1.0; }
                }
                else { fx = 0.0; fy = 0.0; }   // integer remap only — no interpolation at all

                for (int c = 0; c < 3; c++)
                {
                    double v00 = SampleOriented(s, sw, sh, ofsX, ofsY, srcW, srcH, cal, x0, y0, c);
                    if (fx == 0.0 && fy == 0.0) { d[di + c] = (float)v00; continue; }
                    double v10 = SampleOriented(s, sw, sh, ofsX, ofsY, srcW, srcH, cal, x0 + 1, y0, c);
                    double v01 = SampleOriented(s, sw, sh, ofsX, ofsY, srcW, srcH, cal, x0, y0 + 1, c);
                    double v11 = SampleOriented(s, sw, sh, ofsX, ofsY, srcW, srcH, cal, x0 + 1, y0 + 1, c);
                    double top = v00 * (1 - fx) + v10 * fx;
                    double bot = v01 * (1 - fx) + v11 * fx;
                    d[di + c] = (float)(top * (1 - fy) + bot * fy);
                }
            }
        });
        return dst;
    }

    /// <summary>One channel of the oriented frame at integer (ox,oy), read out of the source
    /// slice. Clamped to the slice as a guard — a correctly sized slice always contains it.</summary>
    private static double SampleOriented(float[] s, int sw, int sh, int ofsX, int ofsY,
                                         int srcW, int srcH, FrameParams cal, int ox, int oy, int c)
    {
        var (fx, fy) = OrientedToSource(ox, oy, srcW, srcH, cal);
        int px = Math.Clamp((int)Math.Round(fx) - ofsX, 0, sw - 1);
        int py = Math.Clamp((int)Math.Round(fy) - ofsY, 0, sh - 1);
        return s[(py * sw + px) * 3 + c];
    }

    // ── coordinate helpers ───────────────────────────────────────────────────────
    private static (int W, int H) OrientedSize(int srcW, int srcH, FrameParams cal)
        => (((cal.QuarterTurns % 4) + 4) % 4) % 2 == 1 ? (srcH, srcW) : (srcW, srcH);

    private static (int X, int Y) CropOrigin(int ow, int oh, FrameParams cal)
    {
        if (cal.CropRect is not { } r) return (0, 0);
        return (Math.Max(0, (int)Math.Round(r.X * ow)), Math.Max(0, (int)Math.Round(r.Y * oh)));
    }

    /// <summary>The rotation's output→input map — the same expression
    /// <see cref="Geometry.ApplyRotation"/> uses, including the scipy axis-order sign flip.</summary>
    private static (double X, double Y) RotatedToOriented(double xo, double yo, int w, int h, double degrees)
    {
        if (degrees == 0.0) return (xo, yo);
        double cx = (w - 1) / 2.0, cy = (h - 1) / 2.0;
        double th = degrees * Math.PI / 180.0;
        double cos = Math.Cos(th), sin = Math.Sin(th);
        double dx = xo - cx, dy = yo - cy;
        return (cx + dx * cos + dy * sin, cy - dx * sin + dy * cos);
    }

    /// <summary>Oriented-frame coordinate → source coordinate: undo flipV, flipH, then the 90°
    /// turns, in reverse of <see cref="Geometry.ApplyOrientation"/>'s order.</summary>
    private static (double X, double Y) OrientedToSource(double x, double y, int srcW, int srcH,
                                                         FrameParams cal)
    {
        int k = ((cal.QuarterTurns % 4) + 4) % 4;
        var (w, h) = OrientedSize(srcW, srcH, cal);   // dims at the oriented end
        if (cal.FlipV) y = h - 1 - y;
        if (cal.FlipH) x = w - 1 - x;
        // Undo each 90° CW turn. Forward was: (x,y) in (w,h) → (h-1-y, x) in (h,w).
        for (int i = 0; i < k; i++)
        {
            double nx = y, ny = w - 1 - x;   // inverse of the above, with (w,h) the CURRENT dims
            x = nx; y = ny;
            (w, h) = (h, w);
        }
        return (x, y);
    }
}
