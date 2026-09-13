using OpenRevelare.ColorManagement;
using OpenRevelare.Core;
using Xunit;

namespace OpenRevelare.Tests;

/// <summary>
/// D-031: an HDR roll has exactly one SDR rendition — the asymptote-1 member of its own shoulder
/// family, print stock off — and every SDR surface (film strip, catalog cover, contact sheet,
/// gain-map base) draws that one. The contact sheet's HDR file is the composed SDR page with the
/// frames swapped for their extended renders at the same geometry, the paper pinned at SDR white.
/// </summary>
public sealed class SdrRenditionTests
{
    private const double Peak = 1000d;   // +2.3 stops

    /// <summary>On an SDR roll nothing is cloned or touched: the surfaces stay bit-identical to
    /// what they were before HDR existed.</summary>
    [Fact]
    public void Sdr_params_are_their_own_rendition()
    {
        var p = new FrameParams { OutputSpace = "AdobeRGB", PrintLut = "kodak2383", HdrPeakNits = 0d };
        Assert.Same(p, p.SdrRendition());
        Assert.Same(p, p.SdrRendition(ColorSpaces.DisplayP3));
        // A stored peak this build cannot honour resolves to SDR (FrameParams.ResolvedOutputTarget)
        // and so is SDR here too.
        var low = new FrameParams { HdrPeakNits = OutputTarget.ReferenceWhiteNits };
        Assert.Same(low, low.SdrRendition());
    }

    /// <summary>The HDR rendition's SDR counterpart: peak gone, print LUT gone, base space as
    /// asked (sRGB by default), everything else — the picture — carried over; the original is
    /// left alone.</summary>
    [Fact]
    public void Hdr_params_drop_peak_and_print_lut_and_take_the_base_space()
    {
        var p = new FrameParams
        {
            OutputSpace = "AdobeRGB", PrintLut = "kodak2383", HdrPeakNits = Peak,
            ExposureEv = 0.7, SprocketEnabled = true,
        };
        FrameParams q = p.SdrRendition();
        Assert.NotSame(p, q);
        Assert.False(q.ResolvedOutputTarget.IsExtended);
        Assert.Equal(0d, q.HdrPeakNits);
        Assert.Equal("", q.PrintLut);
        Assert.Equal(ColorSpaces.Srgb.Name, q.OutputSpace);
        Assert.Equal(0.7, q.ExposureEv);
        Assert.True(q.SprocketEnabled);
        Assert.Equal(ColorSpaces.DisplayP3.Name, p.SdrRendition(ColorSpaces.DisplayP3).OutputSpace);
        // Untouched.
        Assert.Equal(Peak, p.HdrPeakNits);
        Assert.Equal("kodak2383", p.PrintLut);
        Assert.Equal("AdobeRGB", p.OutputSpace);
    }

    /// <summary>What the strip and the cover render is the SDR terminal — normalized, profile
    /// encoded — and below the knee it is the extended render's own numbers (D-021), which is
    /// what makes it the same picture and not a different grade.</summary>
    [Fact]
    public void Sdr_rendition_renders_normalized_and_agrees_with_the_extended_render_below_the_knee()
    {
        var hdrParams = new FrameParams { OutputSpace = "sRGB", PrintLut = "", HdrPeakNits = Peak };
        using var cmm = new LittleCmsEngine();
        RenderedFrame hdr = Pipeline.Render(Ramp(), hdrParams, ColorPipelineVersion.ManagedV2, cmm);
        RenderedFrame sdr = Pipeline.Render(Ramp(), hdrParams.SdrRendition(), ColorPipelineVersion.ManagedV2, cmm);

        Assert.Equal(NumericRange.Extended, hdr.Encoding.Range);
        Assert.Equal(NumericRange.Normalized, sdr.Encoding.Range);
        Assert.Equal(TransferState.ProfileEncoded, sdr.Encoding.Transfer);
        Assert.All(sdr.Pixels.Data, v => Assert.InRange(v, 0f, 1f));

        float[] sdrLinear = (float[])sdr.Pixels.Data.Clone();
        OutputRender.Decode(sdrLinear, ColorSpaces.Srgb);
        bool sawHighlight = false, sawShadow = false;
        for (int i = 0; i < sdrLinear.Length; i++)
        {
            float h = hdr.Pixels.Data[i];
            if (h > 1f) { sawHighlight = true; continue; }        // above white only the extended one keeps it
            if (h < HighlightRolloff.Knee)
            {
                sawShadow = true;
                Assert.InRange(sdrLinear[i], h - 2e-3f, h + 2e-3f);   // one family, same numbers below the knee
            }
        }
        Assert.True(sawHighlight, "fixture must reach above diffuse white");
        Assert.True(sawShadow, "fixture must have values below the knee");
    }

    /// <summary>The HDR sheet: cells replaced by the extended thumbnails at the composer's origin,
    /// the surround linearised and left at or below 1.0, the gaps between cells untouched.</summary>
    [Fact]
    public void Extended_sheet_swaps_the_cells_and_pins_the_paper()
    {
        // A 2×1 grid of 3×2 cells with a 1 px gap, placed at (2, 3) on a 12×9 page.
        var layout = new ContactSheet.Layout { Cols = 2, Rows = 1, ThumbW = 3, ThumbH = 2, GapX = 1, GapY = 0, Count = 2 };
        const int gridX = 2, gridY = 3;
        var page = new ImageBuffer(12, 9);
        Array.Fill(page.Data, 0.5f);                                    // sRGB-encoded mid paper
        // The gap column carries a value of its own, to prove it is not touched.
        for (int y = 0; y < layout.ThumbH; y++)
        {
            int o = ((gridY + y) * page.Width + gridX + layout.ThumbW) * 3;
            page.Data[o] = page.Data[o + 1] = page.Data[o + 2] = 0.25f;
        }
        var hot = new ImageBuffer(6, 4);
        Array.Fill(hot.Data, 3f);                                       // a highlight three times white
        var dim = new ImageBuffer(6, 4);
        Array.Fill(dim.Data, 0.1f);

        ImageBuffer sheet = ContactSheet.WithExtendedCells(page, ColorSpaces.Srgb, [hot, dim], layout, gridX, gridY);

        Assert.Equal(page.Width, sheet.Width);
        Assert.Equal(page.Height, sheet.Height);
        float paper = 0.5f;
        float[] one = { paper };
        OutputRender.Decode(one, ColorSpaces.Srgb);
        float paperLinear = one[0];
        float gap = 0.25f;
        float[] g = { gap };
        OutputRender.Decode(g, ColorSpaces.Srgb);
        for (int y = 0; y < sheet.Height; y++)
        for (int x = 0; x < sheet.Width; x++)
        {
            float v = sheet.Data[(y * sheet.Width + x) * 3];
            bool inRows = y >= gridY && y < gridY + layout.ThumbH;
            if (inRows && x >= gridX && x < gridX + layout.ThumbW) Assert.Equal(3f, v);
            else if (inRows && x >= gridX + layout.ThumbW + layout.GapX && x < gridX + layout.Width) Assert.Equal(0.1f, v, 6);
            else if (inRows && x == gridX + layout.ThumbW) Assert.Equal(g[0], v, 6);   // the gap, linearised, not pasted over
            else Assert.Equal(paperLinear, v, 6);
        }
        Assert.All(page.Data, v => Assert.True(v is 0.5f or 0.25f));   // the SDR page is not modified
    }

    /// <summary>A page encoded in primaries other than the carrier's cannot just be linearised
    /// into it; the sheet is always sRGB and the guard says so if that ever changes.</summary>
    [Fact]
    public void Extended_sheet_refuses_a_page_in_foreign_primaries()
    {
        var layout = new ContactSheet.Layout { Cols = 1, Rows = 1, ThumbW = 1, ThumbH = 1, GapX = 0, GapY = 0, Count = 1 };
        Assert.Throws<ArgumentException>(() =>
            ContactSheet.WithExtendedCells(new ImageBuffer(1, 1), ColorSpaces.DisplayP3, [new ImageBuffer(1, 1)], layout, 0, 0));
    }

    /// <summary>The pair the export writes — SDR page and its extended sibling — makes a gain map
    /// that is empty on the paper and carries the highlights only inside the frames: the sheet
    /// is one picture at two headrooms, which is what a gain-map reader assumes of it.</summary>
    [Fact]
    public void Extended_sheet_gain_map_is_empty_outside_the_frames()
    {
        var layout = new ContactSheet.Layout { Cols = 1, Rows = 1, ThumbW = 4, ThumbH = 2, GapX = 0, GapY = 0, Count = 1 };
        const int gridX = 1, gridY = 1;
        var hdrParams = new FrameParams { OutputSpace = "sRGB", PrintLut = "", HdrPeakNits = Peak };
        using var cmm = new LittleCmsEngine();
        RenderedFrame hdrCell = Pipeline.Render(Ramp(), hdrParams, ColorPipelineVersion.ManagedV2, cmm);
        RenderedFrame sdrCell = Pipeline.Render(Ramp(), hdrParams.SdrRendition(), ColorPipelineVersion.ManagedV2, cmm);

        // The SDR page as the composer would leave it: paper, with the SDR cell pasted in.
        var page = new ImageBuffer(6, 4);
        Array.Fill(page.Data, 0.9f);
        ContactSheet.PasteCells(page, [sdrCell.Pixels], layout, gridX, gridY);
        ImageBuffer extended = ContactSheet.WithExtendedCells(page, ColorSpaces.Srgb, [hdrCell.Pixels], layout, gridX, gridY);

        HdrGainMap map = HdrGainMap.Compute(Typed(page, sdr: true), Typed(extended, sdr: false), ColorSpaces.Srgb, hdrParams.ResolvedOutputTarget);

        Assert.True(map.Metadata.HdrCapacityMax > 0.5f, "the frame's highlight must reach the map");
        bool sawGain = false;
        for (int y = 0; y < page.Height; y++)
        for (int x = 0; x < page.Width; x++)
        {
            float code = map.Map.Data[(y * page.Width + x) * 3];
            bool inCell = y >= gridY && y < gridY + layout.ThumbH && x >= gridX && x < gridX + layout.ThumbW;
            if (!inCell) Assert.Equal(0f, code);          // paper: no gain at all
            else if (code > 0f) sawGain = true;
        }
        Assert.True(sawGain);
    }

    private static RenderedFrame Typed(ImageBuffer pixels, bool sdr)
    {
        ColorProfileRef profile = sdr
            ? BuiltInColorProfiles.Srgb(ProfileRole.Output)
            : BuiltInColorProfiles.LinearExtendedSrgb(ProfileRole.Output);
        var encoding = sdr
            ? new CharacterizedPixelEncoding(profile, ColorReference.DisplayReferred, TransferState.ProfileEncoded, NumericRange.Normalized)
            : new CharacterizedPixelEncoding(profile, ColorReference.SceneReferred, TransferState.LinearInProfilePrimaries, NumericRange.Extended);
        var recipe = new OutputRecipe(
            ColorPipelineVersion.ManagedV2, profile, RenderingIntent.RelativeColorimetric,
            blackPointCompensation: false, "test", printLutIdentity: string.Empty, pixelProfileMismatch: false);
        return new RenderedFrame(pixels, encoding, recipe, RenderFingerprint.ComputeManaged(pixels, encoding, recipe));
    }

    /// <summary>A grey ramp from a highlight above the knee down to the toe — the neutral fixture
    /// of the gain-map tests.</summary>
    private static WorkingFrame Ramp()
    {
        var pixels = new ImageBuffer(4, 2,
        [
            0.81f,  0.81f,  0.81f,
            0.63f,  0.63f,  0.63f,
            0.42f,  0.42f,  0.42f,
            0.24f,  0.24f,  0.24f,
            0.13f,  0.13f,  0.13f,
            0.055f, 0.055f, 0.055f,
            0.02f,  0.02f,  0.02f,
            0.008f, 0.008f, 0.008f,
        ])
        {
            SourceQuantisationStep = 1.0 / 65535.0,
        };
        var originalEncoding = new UncharacterizedPixelEncoding(
            CaptureKind.Synthetic,
            "test:sdr-rendition-negative-v1",
            CompatibilityPolicy.LegacyTreatNumbersAsWorking,
            TransferState.Unknown,
            NumericRange.Extended);
        var source = new SourceDescriptor(
            "test:sdr-rendition-negative-v1",
            "sdr-rendition synthetic negative",
            originalEncoding,
            "test-generated RGB float32");
        return new WorkingFrame(
            pixels,
            WorkingSpaceId.LinearAcesCgV1,
            WorkingAdmission.LegacyUncharacterizedPassthrough,
            source);
    }
}
