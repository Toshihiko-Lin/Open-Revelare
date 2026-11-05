using System.Reflection;
using System.Text.Json.Nodes;
using OpenRevelare.ColorManagement;
using OpenRevelare.Core;
using OpenRevelare.Gui.Models;
using OpenRevelare.Gui.ViewModels;
using Xunit;

namespace OpenRevelare.Tests;

public class PositiveFilmTests
{
    private static readonly double[] DMin = { 0.10, 0.15, 0.12 };
    private static readonly double[] DMax = { 2.20, 2.35, 2.25 };

    [Fact]
    public void Endpoints_PositiveFilm_MapsDMinToWhiteAndDMaxToBlack()
    {
        var ep = DensityEndpoints.FromMeasured(DMax, FrameParams.OutputRange, DMin, isPositiveFilm: true);

        for (int c = 0; c < 3; c++)
        {
            // D_min maps to 0.0 (Cineon White 1032)
            double adjAtMin = ep.Apply(c, DMin[c]);
            Assert.Equal(0.0, adjAtMin, 6);
            Assert.Equal(FrameParams.CineonWhiteCode, ep.CodeOf(c, DMin[c]), 5);
            Assert.Equal(DMin[c], ep.Invert(c, 0.0), 6);

            // D_max maps to -OutputRange (Cineon Black 95)
            double adjAtMax = ep.Apply(c, DMax[c]);
            Assert.Equal(-FrameParams.OutputRange, adjAtMax, 6);
            Assert.Equal(FrameParams.CineonBlackCode, ep.CodeOf(c, DMax[c]), 5);
            Assert.Equal(DMax[c], ep.Invert(c, -FrameParams.OutputRange), 6);

            // Mid density lands at mid Cineon code (563.5)
            double midD = (DMin[c] + DMax[c]) / 2.0;
            double midCode = (FrameParams.CineonWhiteCode + FrameParams.CineonBlackCode) / 2.0;
            Assert.Equal(midCode, ep.CodeOf(c, midD), 5);
        }
    }

    [Fact]
    public void Endpoints_PositiveFilm_GreyCard_LandsOn470InEveryChannel()
    {
        // On positive film, grey card is darker (higher density) than the clear highlight base
        double[] grey = { DMin[0] + 0.65, DMin[1] + 0.72, DMin[2] + 0.68 };

        double[] dMax = DensityEndpoints.HighlightFromNeutralAtCode(
            grey, DMin, FrameParams.CineonGreyCode, isPositiveFilm: true);

        var ep = DensityEndpoints.FromMeasured(dMax, FrameParams.OutputRange, DMin, isPositiveFilm: true);

        for (int c = 0; c < 3; c++)
        {
            Assert.Equal(FrameParams.CineonGreyCode, ep.CodeOf(c, grey[c]), 5);
            Assert.True(dMax[c] > grey[c], "D_max must be deeper than mid grey");
        }
    }

    [Fact]
    public void Endpoints_PositiveFilm_GreyCard_RejectsCardNotDenserThanBase()
    {
        double[] greyOnBase = { DMin[0], DMin[1] + 0.5, DMin[2] + 0.5 };
        Assert.Throws<ArgumentException>(() =>
            DensityEndpoints.HighlightFromNeutralAtCode(greyOnBase, DMin, FrameParams.CineonGreyCode, isPositiveFilm: true));
    }

    [Fact]
    public void Inversion_PositiveFilm_HigherTransmissionIsBrighter()
    {
        var calPos = new FrameParams
        {
            IsPositiveFilm = true,
            DMinPerChannel = (double[])DMin.Clone(),
            DMaxPerChannel = (double[])DMax.Clone(),
        };

        var calNeg = new FrameParams
        {
            IsPositiveFilm = false,
            DMinPerChannel = (double[])DMin.Clone(),
            DMaxPerChannel = (double[])DMax.Clone(),
        };

        // Create an image with two pixels: bright transmission (0.8) and low transmission (0.01)
        var img = new ImageBuffer(2, 1, new float[]
        {
            0.8f, 0.8f, 0.8f,
            0.01f, 0.01f, 0.01f
        });

        var outPos = Inversion.Invert(img, calPos);
        var outNeg = Inversion.Invert(img, calNeg);

        float posBright = outPos.Data[0];
        float posDark = outPos.Data[3];
        Assert.True(posBright > posDark, "On positive film, higher transmission must yield brighter output");

        float negBright = outNeg.Data[0];
        float negDark = outNeg.Data[3];
        Assert.True(negBright < negDark, "On negative film, higher transmission must yield darker output");
    }

    [Fact]
    public void Monochrome_PositiveFilm_CollapsesCorrectly()
    {
        var cal = new FrameParams
        {
            Monochrome = true,
            IsPositiveFilm = true,
            DMinPerChannel = new[] { 0.10, 0.15, 0.12 },
            DMaxPerChannel = new[] { 2.10, 2.25, 2.20 },
        };

        var collapsed = Monochrome.Collapse(cal);
        Assert.True(collapsed.IsPositiveFilm);
        Assert.True(collapsed.Monochrome);
        Assert.Equal(0.15, collapsed.DMinPerChannel[0]);
        Assert.Equal(0.15, collapsed.DMinPerChannel[1]);
        Assert.Equal(0.15, collapsed.DMinPerChannel[2]);

        var ep = DensityEndpoints.For(collapsed);
        Assert.Equal(FrameParams.CineonWhiteCode, ep.CodeOf(0, 0.15), 5);
        Assert.Equal(FrameParams.CineonBlackCode, ep.CodeOf(0, 2.25), 5);
    }

    [Fact]
    public void Project_PositiveFilm_RoundTripsThroughJson()
    {
        string path = Path.Combine(Path.GetTempPath(), $"positive_test_{Guid.NewGuid():N}.ncproj");
        try
        {
            var p = new FrameParams
            {
                IsPositiveFilm = true,
                DMinPerChannel = new[] { 0.15, 0.18, 0.16 },
                DMaxPerChannel = new[] { 2.40, 2.50, 2.45 },
            };

            var frame = new Project.Frame
            {
                SourcePath = "/dummy/slide.arw",
                Params = p
            };

            var data = new Project.Data
            {
                Meta = new Project.RollMeta(),
                Frames = new List<Project.Frame> { frame }
            };

            Project.Save(path, data);

            var loaded = Project.Load(path);
            Assert.Single(loaded.Frames);
            Assert.True(loaded.Frames[0].Params.IsPositiveFilm);
            Assert.Equal(0.15, loaded.Frames[0].Params.DMinPerChannel[0], 5);
            Assert.Equal(2.40, loaded.Frames[0].Params.DMaxPerChannel[0], 5);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void Project_LegacyWithoutPositiveField_DefaultsToFalse()
    {
        string path = Path.Combine(Path.GetTempPath(), $"legacy_test_{Guid.NewGuid():N}.ncproj");
        try
        {
            var jsonNode = new JsonObject
            {
                ["version"] = 4,
                ["roll_meta"] = new JsonObject(),
                ["frames"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["source_path"] = "/dummy/neg.arw",
                        ["monochrome"] = false,
                        // "is_positive_film" is omitted as in older projects
                    }
                }
            };

            File.WriteAllText(path, jsonNode.ToJsonString());

            var loaded = Project.Load(path);
            Assert.Single(loaded.Frames);
            Assert.False(loaded.Frames[0].Params.IsPositiveFilm);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    // ══ Automatic calibration on positive film ═══════════════════════════════════
    //
    // The automatic chain (the 自动（整卷）/ 自动（单张）buttons → AutoInvertRollCoreAsync /
    // AutoInvertCurrentFrame) was written for negatives and only its CONSUMERS learned about
    // positive film: DensityEndpoints maps the pair the other way round. The measurements
    // themselves — which end is the film base, which end the densest content, and above all in
    // WHICH DOMAIN the densities are measured — had to keep matching what the render does.
    //
    // The tests below drive the real view-model command, not a reimplementation of it, because
    // the failure being guarded against is precisely a mismatch between the measurement path and
    // the render path. A hand-rolled "measure like the auto chain would" test cannot see it.

    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    private static void SetField(object target, string field, object? value) =>
        target.GetType().GetField(field, Private)!.SetValue(target, value);

    /// <summary>A camera-scanned slide: light board around the film, a dense (unexposed) rebate,
    /// and picture content from a cool highlight through a warm midtone to a deep shadow. Written
    /// in the linear transmittance domain the decoders hand the pipeline.</summary>
    private static ImageBuffer SlideScan(int w = 240, int h = 180)
    {
        var data = new float[w * h * 3];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                (double R, double G, double B) t;
                if (y < h * 0.06 || x >= w * 0.94) t = (1.000, 0.990, 0.985);       // light board
                else if (x < w * 0.04 || y >= h * 0.96) t = (0.004, 0.005, 0.006); // dense rebate
                else
                {
                    double u = (x - w * 0.04) / (w * 0.90);
                    t = u < 0.33 ? (0.740, 0.790, 0.840)   // cool highlight  = the white end
                      : u < 0.66 ? (0.300, 0.280, 0.250)   // warm midtone
                      : (0.020, 0.021, 0.023);             // deep shadow     = the black end
                }
                int i = (y * w + x) * 3;
                data[i] = (float)t.R; data[i + 1] = (float)t.G; data[i + 2] = (float)t.B;
            }
        return new ImageBuffer(w, h, data);
    }

    private static WorkingFrame AsWorkingFrame(ImageBuffer pixels)
    {
        var encoding = new UncharacterizedPixelEncoding(
            CaptureKind.Synthetic, "test:positive-auto",
            CompatibilityPolicy.LegacyTreatNumbersAsWorking,
            TransferState.Unknown, NumericRange.Extended);
        var source = new SourceDescriptor(
            "test:positive-auto", "synthetic slide scan", encoding, "test-generated RGB float32");
        return new WorkingFrame(pixels, WorkingSpaceId.LinearAcesCgV1,
            WorkingAdmission.LegacyUncharacterizedPassthrough, source);
    }

    /// <summary>Run the real 自动（单张）button path over a synthetic slide and return the view
    /// model, so a caller can read whichever half of the result it is about.</summary>
    private static MainViewModel RunAutomaticCalibration(double[,]? decoupleMatrix,
                                                         ImageBuffer? scan = null)
    {
        var vm = new MainViewModel();
        var frame = new RollFrame("/nonexistent/slide.tif")
        {
            Params = new FrameParams { IsPositiveFilm = true },
        };
        vm.Frames.Add(frame);
        // The auto chain reads the decoded preview, the crop and the film-type flags; set them the
        // way a loaded positive frame would have them. _paramsLoaded is false until the (async)
        // frame switch settles, and CommitLiveParams refuses to write while it is — so it is set
        // here, after the switch has been kicked off.
        SetField(vm, "_previewWorking", AsWorkingFrame(scan ?? SlideScan()));
        SetField(vm, "_isPositiveFilm", true);
        SetField(vm, "_monochrome", false);
        vm.CurrentFrame = frame;
        if (decoupleMatrix is not null) SetField(vm, "_decoupleMatrix", decoupleMatrix);
        SetField(vm, "_paramsLoaded", true);
        // A user-set board cut keeps AutoBoardCut off the disk: a slide scan's board is a light
        // panel, and this is the value the import pass would have measured anyway.
        vm.SprocketEnabled = true;
        vm.SprocketThreshold = 0.95;

        vm.AutoInvertCurrentFrame();
        return vm;
    }

    private static double CodeAt(double[] dMin, double[] dMax, int channel, double transmittance)
    {
        var ep = DensityEndpoints.FromMeasured(
            dMax, FrameParams.OutputRange, dMin, isPositiveFilm: true);
        return ep.CodeOf(channel, -Math.Log10(Math.Max(transmittance, 1e-10)));
    }

    /// <summary>
    /// THE REGRESSION THIS FILE EXISTS FOR: 自动（单张）on a slide roll must put the picture's
    /// HIGHLIGHT on Cineon white and its deepest SHADOW on Cineon black.
    ///
    /// It guards the polarity of the whole automatic chain, not one formula. A negative's auto
    /// chain measures "the film base" (brightest) into D_min and "the densest content" into D_max;
    /// on a slide those two measurements are still the brightest and the densest, but D_min is now
    /// the WHITE end, so the pair has to come out as (highlight, shadow) and land 1032/95. If the
    /// chain were left on negative semantics — or if the measured pair were written to the wrong
    /// fields — the picture comes back inverted or flat, which is exactly what "自动校准在正片上
    /// 不工作" looks like.
    /// </summary>
    [Fact]
    public void AutoCalibration_PositiveFilm_PutsHighlightOnWhiteAndShadowOnBlack()
    {
        MainViewModel vm = RunAutomaticCalibration(decoupleMatrix: null);
        double[] dMin = vm.DMinPerChannel, dMax = vm.DMaxPerChannel;

        // The white end is the film's highlight, the black end its deepest shadow — and the pair is
        // ordered the same way round as any density pair (D_max is deeper even though it now reads
        // as black), so FrameParams.Validate's invariant still holds.
        Assert.True(dMax[0] > dMin[0] && dMax[1] > dMin[1] && dMax[2] > dMin[2],
            $"black end must be denser than white end, got D_min={Fmt(dMin)} D_max={Fmt(dMax)}");

        for (int c = 0; c < 3; c++)
        {
            // White belongs to the brightest material in the frame — on a scan that still shows the
            // light table, that is the table. (The picture's own highlight then sits just under
            // white; cropping the frame is what puts the film's clear base on 1032 instead, which
            // is why the UI recommends cropping before auto calibration.)
            double brightest = c switch { 0 => 1.000, 1 => 0.990, _ => 0.985 };
            double shadow = c switch { 0 => 0.020, 1 => 0.021, _ => 0.023 };
            // White is pinned to Cineon white within a code or two. Not bit-exact: when a channel's
            // base sits at the clipping ceiling it carries no colour information and its colour is
            // taken from the reliable channels (FilmBase.ClampToPhysicalBase), which moves that
            // channel's white end by a fraction of a code. One or two codes of white is the honest
            // tolerance; the cast that mattered was tenths of a density.
            Assert.InRange(CodeAt(dMin, dMax, c, brightest), FrameParams.CineonWhiteCode - 4, FrameParams.CineonWhiteCode + 4);
            // Black is pinned to within a band, not exactly: a positive's three channels share ONE
            // span (see Endpoints_PositiveFilm_ShareOneSpanSoTheBlackEndCarriesNoColour), so the
            // channel whose measured span was longest lands a little above 95 and the shortest a
            // little below. Bands of tens of codes around black are the normal, intended spread —
            // what must not happen is the shadow drifting up into the midtones.
            Assert.InRange(CodeAt(dMin, dMax, c, shadow), 40, 160);
        }

        // The endpoints must be the ones that actually reach the picture, not just the live
        // controls: this is what a thumbnail, an export and the next frame switch all read.
        Assert.True(vm.CurrentFrame!.Params.IsPositiveFilm,
            "auto calibration must not clear the roll's film type");
        Assert.Equal(dMin, vm.CurrentFrame.Params.DMinPerChannel);
        Assert.Equal(dMax, vm.CurrentFrame.Params.DMaxPerChannel);
    }

    /// <summary>
    /// The view-model half of the perforation rule: 自动（单张）on a slide with visible perforations
    /// must place the white end on the light through the HOLES, not on the brighter coloured
    /// picture patch the old sampled-highlight path would take. The fixture's block is brighter
    /// than the holes, so an unwired or reverted chain reads a lower D_min and fails the band.
    /// </summary>
    [Fact]
    public void AutoCalibration_PositiveFilm_TakesTheWhiteEndFromThePerforations()
    {
        ImageBuffer strip = PositivePerforationTests.Strip(0.60, (1.00, 0.90, 0.70));

        MainViewModel vm = RunAutomaticCalibration(decoupleMatrix: null, scan: strip);

        double expected = -Math.Log10(0.60);   // holes are 0.60 in every channel
        for (int c = 0; c < 3; c++)
            Assert.InRange(vm.DMinPerChannel[c], expected - 0.01, expected + 0.01);
        Assert.True(vm.StatusText.Contains("齿孔"), $"status should say the route, got '{vm.StatusText}'");
    }

    /// <summary>
    /// The black-end half of the same rule: 自动（单张）on a slide with an unexposed perforation band
    /// must put the black end on the film's known black (0.004 here), not on the picture's 0.15
    /// midtone. A scene solve alone would land near 0.82 D; the physical edge is ~2.40 D.
    /// </summary>
    [Fact]
    public void AutoCalibration_PositiveFilm_TakesTheBlackEndFromThePerforations()
    {
        ImageBuffer strip = PositivePerforationTests.Strip(0.60, (1.00, 0.90, 0.70));

        MainViewModel vm = RunAutomaticCalibration(decoupleMatrix: null, scan: strip);

        // D_min = -log10(0.60) = 0.2218 plus the unexposed span log10(0.60/0.004) = 2.176.
        foreach (double d in vm.DMaxPerChannel)
            Assert.InRange(d, 2.37, 2.43);
        Assert.True(vm.StatusText.Contains("黑端取自片边未曝光黑"),
            $"status should say the black route, got '{vm.StatusText}'");
    }

    /// <summary>
    /// A band lifted to 0.20 (light-table spill) is not an unexposed black. The perforation path
    /// declines, the scene estimate keeps the black end, and the status says so — reporting 2.4 D
    /// from a lifted band would crush every shadow on the frame.
    /// </summary>
    [Fact]
    public void AutoCalibration_PositiveFilm_ContaminatedBandFallsBackToSceneBlack()
    {
        ImageBuffer strip = PositivePerforationTests.Strip(0.60, bridgeLevel: 0.20);

        MainViewModel vm = RunAutomaticCalibration(decoupleMatrix: null, scan: strip);

        Assert.True(vm.StatusText.Contains("黑端取自画面暗部"),
            $"status should say the fallback route, got '{vm.StatusText}'");
        for (int c = 0; c < 3; c++)
            Assert.True(vm.DMaxPerChannel[c] > vm.DMinPerChannel[c],
                "black end must stay deeper than the white end");
    }

    /// <summary>
    /// THE BUG THAT MADE THE BUTTON LOOK BROKEN ON SLIDES ONLY: the automatic chain measured in a
    /// different colour domain than the render uses.
    ///
    /// A Path-A capture calibration (the RGB decoupling a copy-stand workflow produces) is applied
    /// by the negative pipeline and — deliberately — NOT by the positive one: a slide has no orange
    /// mask to undo, so Pipeline.ProcessFrame skips the matrix. The measurement helper that feeds
    /// every automatic and sampled endpoint (MainViewModel.Stage1Source) kept applying it, so on a
    /// slide roll each endpoint was solved from matrixed pixels and then used to invert raw ones.
    /// The numbers still looked plausible, which is what hid it: the white end sat a few codes and a
    /// visible cast off Cineon 1032 instead of on it, and the channel differences that are supposed
    /// to be the highlight balance were the matrix's. Negatives were unaffected because there both
    /// paths apply the matrix — which is why the same button worked until the roll was switched to
    /// positive.
    ///
    /// So: with the capture matrix present, a slide's automatic result must be bit-for-bit the
    /// result without it. That is the invariant, and it is what this test pins.
    /// </summary>
    [Fact]
    public void AutoCalibration_PositiveFilm_MeasuresInTheDomainTheRenderUses()
    {
        // A real decoupling matrix: dye-layer separation, not a row-normalised grey-preserving one,
        // so a coloured subject genuinely moves if the matrix is applied.
        double[,] matrix =
        {
            { 1.35, -0.25, -0.10 },
            { -0.20, 1.30, -0.10 },
            { -0.05, -0.20, 1.25 },
        };

        MainViewModel plain = RunAutomaticCalibration(decoupleMatrix: null);
        MainViewModel matrixed = RunAutomaticCalibration(matrix);

        Assert.Equal(plain.DMinPerChannel, matrixed.DMinPerChannel);
        Assert.Equal(plain.DMaxPerChannel, matrixed.DMaxPerChannel);

        // And the answer is still the correct one, so "equal" is not equal-and-wrong.
        double[] dMin = matrixed.DMinPerChannel, dMax = matrixed.DMaxPerChannel;
        Assert.Equal(FrameParams.CineonWhiteCode, CodeAt(dMin, dMax, 0, 1.000), 0);
        Assert.InRange(CodeAt(dMin, dMax, 0, 0.020), 40, 160);   // shared span: see above
    }

    /// <summary>
    /// The render half of the same invariant, at the level where it is stated: a positive frame
    /// renders identically with and without a Path-A decouple matrix, while a negative frame does
    /// not. Pinning both halves together is what stops the measurement path (a view-model concern)
    /// and the pipeline from drifting apart again — the drift itself is invisible in either half
    /// alone.
    /// </summary>
    [Fact]
    public void Pipeline_PositiveFilm_RendersIdenticallyWithAndWithoutDecoupling()
    {
        ImageBuffer source = SlideScan();
        var positive = new FrameParams
        {
            IsPositiveFilm = true,
            OutputIntent = OutputIntent.None,
            DMinPerChannel = new[] { 0.131, 0.102, 0.076 },
            DMaxPerChannel = new[] { 1.699, 1.678, 1.638 },
            DecoupleMatrix = new double[,] { { 1.35, -0.25, -0.10 }, { -0.20, 1.30, -0.10 }, { -0.05, -0.20, 1.25 } },
        };
        FrameParams positivePlain = positive.Clone();
        positivePlain.DecoupleMatrix = null;

        ImageBuffer withMatrix = Pipeline.ProcessFrame(source, positive);
        ImageBuffer without = Pipeline.ProcessFrame(source, positivePlain);
        Assert.Equal(without.Data, withMatrix.Data);

        var negative = positive.Clone();
        negative.IsPositiveFilm = false;
        FrameParams negativePlain = negative.Clone();
        negativePlain.DecoupleMatrix = null;
        Assert.NotEqual(Pipeline.ProcessFrame(source, negativePlain).Data,
                        Pipeline.ProcessFrame(source, negative).Data);
    }

    private static string Fmt(double[] v) =>
        "[" + string.Join(", ", v.Select(x => x.ToString("F4"))) + "]";

    // ══ Border-inset resolution: the two fixtures ═════════════════════════════════
    //
    // The endpoint detector must pick a border inset from the FRAME, because whether a slide's
    // unexposed (and therefore densest) margin is still in the buffer is a property of the scan,
    // not of the film type. FilmBase.DetectDMaxPerChannelResolvingBorderInset does it in two
    // steps: widen only if excluding the outer band materially shallows the endpoint, then reject
    // the wide answer if it comes out shallower than the picture's own interior tone.
    //
    // The pair below is what makes that testable without a sample roll. 甲 proves the fallback
    // fires; 乙 proves it does not fire unconditionally. Either one alone is worthless: 甲 alone
    // passes if the code always falls back, 乙 alone passes if it never does.

    /// <summary>甲: a flat, tiered picture inside a wide dense margin. The tiers make the endpoint
    /// detector's tail guard withhold the densest tier, so the wide inset returns a MIDTONE that is
    /// shallower than the picture's real deepest tone — the collapse the fallback exists to catch.</summary>
    private static ImageBuffer SlideTieredWideMargin(double margin = 0.12, int w = 240, int h = 180)
        => SlideWithMargin(margin, w, h, (u, _) => u < 0.40 ? (0.740, 0.790, 0.840)
                                         : u < 0.70 ? (0.300, 0.280, 0.250)
                                                    : (0.020, 0.021, 0.023));

    /// <summary>乙: the same wide dense margin around a CONTINUOUS-TONE picture. Real film looks
    /// like this, and here the wide inset is the correct answer — with the margin excluded the
    /// endpoint lands on the picture's own darkest tone instead of on the margin.</summary>
    private static ImageBuffer SlideGradedWideMargin(double margin = 0.12, int w = 240, int h = 180)
        => SlideWithMargin(margin, w, h, (u, _) =>
        {
            // smooth ramp from a bright highlight down to a deep shadow
            double t = 0.86 * Math.Pow(1.0 - 0.985 * u, 3.2) + 0.012;
            return (t, t * 1.01, t * 1.03);
        });

    private static ImageBuffer SlideWithMargin(double margin, int w, int h,
                                               Func<double, double, (double R, double G, double B)> picture)
    {
        var data = new float[w * h * 3];
        int inner = Math.Max(1, (int)(w * (1 - 2 * margin)));
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                (double R, double G, double B) t;
                if (x < w * margin || x >= w * (1 - margin)
                    || y < h * margin || y >= h * (1 - margin))
                    t = (0.004, 0.005, 0.006);           // unexposed margin: densest thing in frame
                else
                {
                    double u = (x - w * margin) / (double)inner;
                    t = picture(Math.Clamp(u, 0, 1), y);
                }
                int i = (y * w + x) * 3;
                data[i] = (float)t.R; data[i + 1] = (float)t.G; data[i + 2] = (float)t.B;
            }
        return new ImageBuffer(w, h, data);
    }

    /// <summary>
    /// 甲 — the fallback MUST fire. The wide inset collapses onto a midtone (0.43 against a picture
    /// that really goes to 1.60), so the resolver has to reject 0.15 and return the tight answer.
    ///
    /// Asserting "deeper than the wide answer" rather than a literal number is what makes this a
    /// guard rather than a golden: it fails if the resolver ever accepts the collapse, and it does
    /// not care which tight-inset number comes back.
    /// </summary>
    [Fact]
    public void BorderInset_TieredPicture_FallsBackWhenTheWideInsetEatsThePicture()
    {
        ImageBuffer img = SlideTieredWideMargin();
        double[] tb = { 0.80, 0.80, 0.80 };

        HighlightEndpointEstimate? wide = FilmBase.DetectDMaxPerChannelFromRollDetailed(
            new[] { img }, tb, 90.0, new[] { img }, null,
            FilmBase.WideBorderInsetForMeasurement, protectIndependentChannelExtrema: true);
        HighlightEndpointEstimate? tight = FilmBase.DetectDMaxPerChannelFromRollDetailed(
            new[] { img }, tb, 90.0, new[] { img }, null,
            FilmBase.TightBorderInsetForMeasurement, protectIndependentChannelExtrema: true);
        HighlightEndpointEstimate? resolved = FilmBase.DetectDMaxPerChannelResolvingBorderInset(
            new[] { img }, tb, 90.0, new[] { img }, null, protectIndependentChannelExtrema: true,
            positiveFilm: true);

        Assert.NotNull(wide);
        Assert.NotNull(tight);
        Assert.NotNull(resolved);

        // The wide answer really is the collapse this test is about (a midtone, ~0.43)...
        Assert.True(tight!.Density[0] - wide!.Density[0] > 1.0,
            $"fixture must make the wide inset collapse, got tight={Fmt(tight.Density)} wide={Fmt(wide.Density)}");
        // ...and the resolver must not have taken it.
        Assert.Equal(tight.Density, resolved!.Density);
        Assert.True(resolved.Density[0] > wide.Density[0] + 1.0,
            $"resolver returned the collapsed midtone: {Fmt(resolved.Density)}");
    }

    /// <summary>
    /// 乙 — the wide inset MUST be kept when it is the right answer. Same wide dense margin, but a
    /// continuous-tone picture, which is what real film looks like: excluding the margin lands the
    /// endpoint on the picture's own deepest tone, so the resolver must return 0.15's answer.
    ///
    /// This is the half that rules out "always fall back": 甲 alone passes if the resolver never
    /// widens, and a resolver that never widens would leave every real slide strip at the washed-out
    /// black end the sample roll measured.
    /// </summary>
    [Fact]
    public void BorderInset_ContinuousTonePicture_KeepsTheWideInset()
    {
        ImageBuffer img = SlideGradedWideMargin();
        double[] tb = { 0.80, 0.80, 0.80 };

        HighlightEndpointEstimate? wide = FilmBase.DetectDMaxPerChannelFromRollDetailed(
            new[] { img }, tb, 90.0, new[] { img }, null,
            FilmBase.WideBorderInsetForMeasurement, protectIndependentChannelExtrema: true);
        HighlightEndpointEstimate? tight = FilmBase.DetectDMaxPerChannelFromRollDetailed(
            new[] { img }, tb, 90.0, new[] { img }, null,
            FilmBase.TightBorderInsetForMeasurement, protectIndependentChannelExtrema: true);
        HighlightEndpointEstimate? resolved = FilmBase.DetectDMaxPerChannelResolvingBorderInset(
            new[] { img }, tb, 90.0, new[] { img }, null, protectIndependentChannelExtrema: true,
            positiveFilm: true);

        Assert.NotNull(wide);
        Assert.NotNull(tight);
        Assert.NotNull(resolved);

        // The margin is dense enough that the two insets disagree — otherwise this fixture would
        // not be exercising the choice at all.
        Assert.True(tight!.Density[0] - wide!.Density[0] > 0.15,
            $"fixture must make the margin dominate the tight inset, got tight={Fmt(tight.Density)} wide={Fmt(wide.Density)}");
        // The wide answer is the one that sees only the picture, so it must be the one kept.
        Assert.Equal(wide.Density, resolved!.Density);
        Assert.True(resolved.Density[0] < tight.Density[0] - 0.15,
            $"resolver fell back to the margin: {Fmt(resolved.Density)}");
    }

    /// <summary>
    /// Real-frame regression, skipped when the sample roll is not present (it is gitignored and is
    /// not shipped): run the resolver over uncropped slide frames and check that the picture's own
    /// deepest tone lands in the black part of the Cineon scale rather than at the washed-out code
    /// the tight inset produces (median 287 on this roll).
    ///
    /// This is the only test in the file that depends on data outside the repo, which is why it
    /// returns instead of failing when <c>SLIDE_ROLL_DIR</c> is unset.
    /// </summary>
    [Fact]
    public void BorderInset_RealSlideRoll_UncroppedFramesLandNearBlack()
    {
        string? roll = Environment.GetEnvironmentVariable("SLIDE_ROLL_DIR");
        if (string.IsNullOrWhiteSpace(roll) || !Directory.Exists(roll)) return;

        var cmm = new OpenRevelare.ColorManagement.LittleCmsEngine();
        var codes = new List<double>();
        foreach (string path in Directory.GetFiles(roll, "*.ARW").OrderBy(f => f))
        {
            OpenRevelare.Core.WorkingFrame wf;
            try
            {
                (wf, _, _) = OpenRevelare.Gui.Services.ImageIo.LoadWorkingPreview(
                    path, 1600, ColorPipelineVersion.ManagedV2, cmm, TiffInputAssumption.Unspecified);
            }
            catch { continue; }

            ImageBuffer img = wf.Pixels;
            double? cut = Sprocket.EstimateSprocketThreshold(img);
            double[]? tb = cut is null ? null : FilmBase.EstimateTBaseByMode(img, cut);
            if (tb is null)
                tb = FilmBase.EstimateTBaseFromRoll(new[] { img }, cut);
            double[] dMin = tb.Select(t => -Math.Log10(Math.Max(t, 1e-10))).ToArray();

            // Independent reference: densest 0.1% of the central 70%. A frame whose reference sits
            // at the encoding ceiling is not measurable at all (the base estimate itself is wrong,
            // e.g. the capture is mostly strip gap) and must not vote — the point of this test is
            // the endpoint decision, not the base estimator.
            double[] interior = DensestTail(img, tb, dMin, 0.15);
            if (interior.Any(v => v - dMin[0] >= FrameParams.RealDensityCeiling - 0.2)) continue;

            HighlightEndpointEstimate? resolved = FilmBase.DetectDMaxPerChannelResolvingBorderInset(
                new[] { img }, tb, 90.0, new[] { img }, cut, protectIndependentChannelExtrema: true,
                positiveFilm: true);
            if (resolved is null) continue;

            double[] dMax = resolved.Density.Select((x, c) => x + dMin[c]).ToArray();
            var ep = DensityEndpoints.FromMeasured(dMax, FrameParams.OutputRange, dMin, isPositiveFilm: true);
            codes.Add(ep.CodeOf(0, interior[0]));
        }

        Assert.True(codes.Count >= 20, $"expected a measurable roll, got {codes.Count} frames ({roll})");

        // The MEDIAN, not every frame: these captures are angled strips holding 2-4 negatives each,
        // so a few frames put no picture in the central window at all and cannot be measured by
        // anything. What must hold is that the roll's typical frame lands in the black part of the
        // scale. With the fixed 0.05 inset this roll's median was ~287 (washed out); a regression to
        // that behaviour fails here.
        codes.Sort();
        double median = codes[codes.Count / 2];
        Assert.InRange(median, 95, 220);
    }

    /// <summary>Co-sited densest tail of a region, ABSOLUTE densities. Independent of the
    /// detector's admission rules on purpose — see the note on FilmBase.InteriorDensestSpan.</summary>
    private static double[] DensestTail(ImageBuffer img, double[] tBase, double[] dMin, double inset)
    {
        int w = img.Width, h = img.Height;
        int x0 = (int)(w * inset), x1 = (int)(w * (1 - inset));
        int y0 = (int)(h * inset), y1 = (int)(h * (1 - inset));
        var totals = new List<(double T, double R, double G, double B)>();
        for (int y = y0; y < y1; y++)
            for (int x = x0; x < x1; x++)
            {
                int i = (y * w + x) * 3;
                double d0 = -Math.Log10(Math.Max(img.Data[i] / tBase[0], 1e-10));
                double d1 = -Math.Log10(Math.Max(img.Data[i + 1] / tBase[1], 1e-10));
                double d2 = -Math.Log10(Math.Max(img.Data[i + 2] / tBase[2], 1e-10));
                if ((d0 + d1 + d2) / 3.0 >= FrameParams.RealDensityCeiling) continue;
                totals.Add(((d0 + d1 + d2) / 3.0, d0, d1, d2));
            }
        if (totals.Count == 0) return dMin;
        totals.Sort((a, b) => b.T.CompareTo(a.T));
        int k = Math.Max(1, (int)(totals.Count * 0.001));
        var res = new double[3];
        for (int i = 0; i < k; i++) { res[0] += totals[i].R; res[1] += totals[i].G; res[2] += totals[i].B; }
        for (int c = 0; c < 3; c++) res[c] = res[c] / k + dMin[c];
        return res;
    }

    // ══ The white end must sit at the frame's bright end ══════════════════════════
    //
    // The base estimators were written for a negative, where the frame margin is bare orange base
    // — the BRIGHTEST material on the film. On a positive the margin is unexposed and therefore
    // dense, so the brightest material is the picture's own highlight or the light table, and the
    // board cut (which is meant to remove the table) can land BELOW the film's clear areas and
    // report a dense interior as "the base". The white end then sits inside the picture and every
    // density above it clips: overexposure by construction. Measured on the real slide roll,
    // DSC02016 read t_base 0.066 where its film's bright end is 0.485 (D_min 1.183 against 0.31).

    /// <summary>A frame whose bright material is at 0.50: clear film across the middle, a dense
    /// band along the bottom that a mis-placed board cut would happily call "the base".</summary>
    private static ImageBuffer SlideWithDenseBand(int w = 200, int h = 140)
    {
        var data = new float[w * h * 3];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                double t = y > h * 0.75 ? 0.055 : 0.50;    // dense band, then clear film
                int i = (y * w + x) * 3;
                data[i] = (float)t; data[i + 1] = (float)t; data[i + 2] = (float)t;
            }
        return new ImageBuffer(w, h, data);
    }

    /// <summary>
    /// THE REGRESSION: a white end estimated below the frame's bright end must be raised to it.
    /// Without this the white point lands inside the picture and the whole frame overexposes,
    /// which is what the report "白点自动分析偏过曝" turned out to be.
    /// </summary>
    [Fact]
    public void WhiteEnd_IsRaisedToTheFramesBrightEndWhenTheBaseEstimateLandedInsideThePicture()
    {
        ImageBuffer img = SlideWithDenseBand();

        // What a mis-placed board cut produces on this frame: the dense band, not the clear film.
        double[] badBase = { 0.055, 0.055, 0.055 };
        double[] fixedBase = FilmBase.KeepWhiteEndAtBrightEnd(badBase, img);

        // The clear film is 0.50 and is the brightest material in the frame, so that is the white
        // end — not the 0.055 the bad estimate reported (which is 0.96 D denser, i.e. most of the
        // picture clipped to white).
        Assert.InRange(fixedBase[0], 0.45, 1.0);
        Assert.True(fixedBase[0] > badBase[0] + 0.4,
            $"white end must move up to the bright end, got {fixedBase[0]:F3} from {badBase[0]:F3}");

        // And the white end that produced the overexposure is caught by the invariant itself:
        // D_min must not be denser than the frame's own bright end.
        double badDMin = -Math.Log10(badBase[0]);
        double brightDMin = -Math.Log10(fixedBase[0]);
        Assert.True(badDMin > brightDMin + 0.5,
            $"the bad base was {badDMin:F2} D against a bright end of {brightDMin:F2} D");
    }

    /// <summary>
    /// The clamp only ever RAISES the white end. A base that is already at or above the frame's
    /// bright end — a scan with nothing in frame brighter than the picture, or a correct pick —
    /// must come back untouched, so this cannot quietly darken rolls that were fine.
    /// </summary>
    [Fact]
    public void WhiteEnd_IsNeverLoweredBelowTheMeasuredBase()
    {
        ImageBuffer img = SlideWithDenseBand();
        double[] correctBase = { 0.90, 0.90, 0.90 };   // brighter than anything in the frame

        double[] result = FilmBase.KeepWhiteEndAtBrightEnd(correctBase, img);

        Assert.Equal(correctBase, result);
    }

    // ══ No mask removal on positive film ═════════════════════════════════════════
    //
    // A negative's per-channel spans ARE the orange mask being divided out — that is what 去色罩
    // means in this model, and it is correct there. A positive has no mask: its clear base, pinned
    // at Cineon white per channel, is already the neutral reference. Whatever per-channel
    // difference the measured black end still carries is dense dye and frame margin — not a film
    // property — and turning it into per-channel contrast tints every tone between the ends.
    //
    // Measured on the real slide roll, frame DSC02015 solved spans of (0.61, 0.93, 1.73) and
    // rendered a deep cyan-blue wash; DSC02019 solved (2.71, 2.63, 2.64) and rendered magenta.
    // With one shared span both render as ordinary neutral scenes.

    /// <summary>
    /// The rule itself: a positive gets ONE span across the three channels (the mean of what was
    /// measured, kept as the least-squares estimate of the film's real contrast), anchored on the
    /// per-channel white end so the base stays neutral.
    /// </summary>
    [Fact]
    public void Endpoints_PositiveFilm_ShareOneSpanSoTheBlackEndCarriesNoColour()
    {
        double[] dMin = { 0.30, 0.10, 0.20 };
        double[] unevenDMax = { dMin[0] + 0.61, dMin[1] + 0.93, dMin[2] + 1.73 };

        double[] shared = DensityEndpoints.SharedSpanEndpoints(unevenDMax, dMin, isPositiveFilm: true);

        // One span, equal in all three channels...
        Assert.Equal(shared[0] - dMin[0], shared[1] - dMin[1], 6);
        Assert.Equal(shared[1] - dMin[1], shared[2] - dMin[2], 6);
        // ...equal to the mean of what was measured...
        Assert.Equal((0.61 + 0.93 + 1.73) / 3.0, shared[0] - dMin[0], 6);
        // ...and anchored on each channel's own white end, so the base stays neutral.
        for (int c = 0; c < 3; c++) Assert.Equal(dMin[c] + (0.61 + 0.93 + 1.73) / 3.0, shared[c], 6);
    }

    /// <summary>
    /// The other half: a NEGATIVE must keep its per-channel spans. Those differences are the orange
    /// mask and the highlight balance — the 去色罩 the negative path exists to do — so flattening
    /// them would be a regression, not a fix.
    /// </summary>
    [Fact]
    public void Endpoints_NegativeFilm_KeepsPerChannelSpans()
    {
        double[] dMin = { 0.10, 0.30, 0.55 };            // orange base: R < G < B
        double[] dMax = { 2.20, 2.35, 2.60 };

        double[] result = DensityEndpoints.SharedSpanEndpoints(dMax, dMin, isPositiveFilm: false);

        Assert.Equal(dMax, result);                      // untouched, byte for byte
        // and the spans really are different, so the assertion above is not vacuous
        Assert.NotEqual(dMax[0] - dMin[0], dMax[2] - dMin[2], 3);
    }

    /// <summary>
    /// End to end through the real 自动（单张）command: after an automatic solve on a positive, the
    /// committed endpoints must have one shared span. This is the shape the user sees — equal spans
    /// means the picture has one colour balance (the base's) instead of three competing ones.
    /// </summary>
    [Fact]
    public void AutoCalibration_PositiveFilm_CommitsSharedSpanEndpoints()
    {
        MainViewModel vm = RunAutomaticCalibration(decoupleMatrix: null);
        double[] dMin = vm.DMinPerChannel, dMax = vm.DMaxPerChannel;

        double s0 = dMax[0] - dMin[0], s1 = dMax[1] - dMin[1], s2 = dMax[2] - dMin[2];
        Assert.Equal(s0, s1, 3);
        Assert.Equal(s1, s2, 3);
        Assert.True(s0 > 0.2, $"span should be a real contrast, got {s0:F3}");
    }

    // ══ The density reference is neutral on a positive ══════════════════════════
    //
    // T_base is a legacy field: it used to BE the film base, and a project written by an older
    // build restores whatever it stored. On a negative that value is at least a real quantity — a
    // mask colour. On a positive there is no mask at all, so a non-neutral reference only subtracts
    // a per-channel constant from every measurement. Measured with a stored (0.82, 0.51, 0.29) on a
    // synthetic slide: the black end read 1.613 / 1.407 / 1.161 where the patch's true density is
    // 1.699 in all three, and the grey card failed outright with one channel at -0.015 D — which is
    // the "采样区不比黑端浓" message, i.e. the grey card appearing not to work at all.

    /// <summary>A slide carrying a neutral grey patch, a clear-base band and a deep shadow, so the
    /// three samplers can each be pointed at a known density.</summary>
    private static ImageBuffer SlideWithGreyPatch(int w = 200, int h = 200)
    {
        var d = new float[w * h * 3];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                double t = y < h * 0.10 ? 0.60                                       // clear base
                         : (y > h * 0.35 && y < h * 0.65 && x > w * 0.35 && x < w * 0.65) ? 0.30  // grey card
                         : y > h * 0.85 ? 0.02                                      // deep shadow
                         : 0.52;
                int i = (y * w + x) * 3;
                d[i] = (float)t; d[i + 1] = (float)t; d[i + 2] = (float)t;
            }
        return new ImageBuffer(w, h, d);
    }

    private static MainViewModel LoadedSlide(int w = 200, int h = 200, double[]? storedBase = null,
                                             bool positive = true)
    {
        var vm = new MainViewModel();
        var frame = new RollFrame("/nonexistent/slide.tif")
        {
            Params = new FrameParams
            {
                IsPositiveFilm = positive,
                TBase = storedBase ?? new[] { 1.0, 1.0, 1.0 },
            },
        };
        vm.Frames.Add(frame);
        SetField(vm, "_previewWorking", AsWorkingFrame(SlideWithGreyPatch(w, h)));
        SetField(vm, "_isPositiveFilm", positive);
        SetField(vm, "_monochrome", false);
        vm.CurrentFrame = frame;
        // Exactly what opening a project does: pulls TBase / D_min / D_max off the frame.
        typeof(MainViewModel).GetMethod("LoadParams", Private)!.Invoke(vm, new object[] { frame.Params });
        SetField(vm, "_paramsLoaded", true);
        return vm;
    }

    private static string Status(MainViewModel vm) => vm.StatusText;

    /// <summary>
    /// THE REGRESSION for "灰卡采样/黑白端采样不工作": a positive roll whose project stores a
    /// non-neutral T_base must still measure against a NEUTRAL reference, because there is no mask
    /// to carry. Without this the black end picks up a fixed per-channel bias and the grey card
    /// throws before writing anything.
    /// </summary>
    [Fact]
    public void Sampling_PositiveFilm_IgnoresAStoredNonNeutralBase()
    {
        // What an older build wrote into the project: a C-41 orange base.
        using MainViewModel vm = LoadedSlide(storedBase: new[] { 0.82, 0.51, 0.29 });

        vm.SampleDMax((0.20, 0.88, 0.50, 0.08));            // the deep shadow band
        Assert.Equal(new[] { 1.699, 1.699, 1.699 }, vm.DMaxPerChannel.Select(v => Math.Round(v, 3)).ToArray());

        vm.SampleNeutralGrey((0.40, 0.40, 0.20, 0.20));     // the neutral card
        // It must have measured, not thrown: the three channels of a neutral patch stay equal, and
        // none of them may come back negative (which is what the stale base used to produce).
        double[] after = vm.DMaxPerChannel;
        Assert.Equal(after[0], after[1], 6);
        Assert.Equal(after[1], after[2], 6);
        Assert.True(after.All(v => v > 0.1),
            $"grey card produced {Fmt(after)} — status was '{Status(vm)}'");
    }

    /// <summary>
    /// The reference rule itself: on a positive the density reference is neutral whatever the view
    /// model's own T_base fields happen to hold, while a negative keeps the stored base (a mask
    /// colour there is a real quantity).
    /// </summary>
    [Fact]
    public void Sampling_PositiveFilm_UsesNeutralReferenceEvenWhenTheLiveFieldsAreNot()
    {
        using MainViewModel positive = LoadedSlide();
        Assert.Equal(new[] { 1.0, 1.0, 1.0 }, Invoke(positive, "TBaseArr"));

        using MainViewModel negative = LoadedSlide(storedBase: new[] { 0.82, 0.51, 0.29 }, positive: false);
        Assert.Equal(new[] { 0.82, 0.51, 0.29 }, Invoke(negative, "TBaseArr"));
    }

    private static double[] Invoke(object target, string method) =>
        (double[])target.GetType().GetMethod(method, Private)!.Invoke(target, null)!;

    /// <summary>
    /// A legacy project stores a non-neutral <c>t_base</c>; the loader folds it into BOTH endpoints
    /// and neutralises the field (Project.RebaseToNeutral), so nothing downstream — samplers
    /// included — ever measures against a phantom base. This pins that migration, because the
    /// samplers' correctness rests on it (see Sampling_PositiveFilm_IgnoresAStoredNonNeutralBase).
    /// </summary>
    [Fact]
    public void Project_LegacyNonNeutralBase_LoadsFoldedIntoTheEndpoints()
    {
        // The same project twice: once with a legacy orange base, once without. Everything else is
        // identical, so the difference between the two loads IS the migration.
        FrameParams WithBase() => LoadLegacy(storeBase: true);
        FrameParams WithoutBase() => LoadLegacy(storeBase: false);

        FrameParams with = WithBase(), without = WithoutBase();

        // (1) The base is neutral afterwards — nothing downstream can measure against a phantom
        //     base, which is what the sampling bug was.
        Assert.Equal(new[] { 1.0, 1.0, 1.0 }, with.TBase);

        // (2) It survives in the endpoints as ABSOLUTE density: -log10(0.82) etc.
        for (int c = 0; c < 3; c++)
        {
            double expected = -Math.Log10(new[] { 0.82, 0.51, 0.29 }[c]);
            Assert.Equal(without.DMinPerChannel[c] + expected, with.DMinPerChannel[c], 6);
            // (3) BOTH ends moved by that same amount — the property that makes the migration
            //     render-identical: span (contrast) and between-channel differences (cast) are
            //     untouched, only the absolute placement moved.
            Assert.Equal(without.DMaxPerChannel[c] + expected, with.DMaxPerChannel[c], 6);
        }
    }

    private static FrameParams LoadLegacy(bool storeBase)
    {
        string path = Path.Combine(Path.GetTempPath(), $"legacybase_{Guid.NewGuid():N}.ncproj");
        try
        {
            var frame = new JsonObject
            {
                ["source_path"] = "/dummy/neg.arw",
                ["monochrome"] = false,
            };
            if (storeBase) frame["t_base"] = new JsonArray(0.82, 0.51, 0.29);
            var json = new JsonObject
            {
                ["version"] = 4,
                ["roll_meta"] = new JsonObject(),
                ["frames"] = new JsonArray { frame },
            };
            File.WriteAllText(path, json.ToJsonString());
            return Project.Load(path).Frames[0].Params;
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    // ══ The white end is a physical transmittance ═══════════════════════════════
    //
    // t_base is a transmittance: it lives in (0, 1]. The estimators only ever rejected <= 0, so a
    // value above 1 could reach D_min = -log10(t) and make it NEGATIVE — the white end then sits
    // above every real pixel, which is overexposure by construction. This is not hypothetical: a
    // real user project (42 cropped frames, 自动（整卷）) held
    //     D_min = [0.297, -0.045, 0.180]  ->  t_base = [0.505, 1.109, 0.661]
    // with green 2.2x red and above unity, and 39 of its 42 frames had a negative channel.
    //
    // Second, subtler defect: KeepWhiteEndAtBrightEnd merged two CO-SITED triples with a per-channel
    // max. A co-sited triple's value is its channel RATIOS (that is the white end's colour), so a
    // per-channel merge invents a third triple belonging to no patch of film — a neutral base can
    // come out with a large spread purely from the splice. It now picks one triple wholesale.

    /// <summary>
    /// A DARK frame: its own bright end is dim, so a stored base that is brighter than the frame
    /// WINS the triple-selection and is then the thing that has to be clamped. (With a bright frame
    /// the estimate would win instead and the clamp would never be exercised — which is exactly the
    /// trap that made an earlier version of this test pass under a mutation.)
    /// </summary>
    private static ImageBuffer DarkSlide(int w = 160, int h = 160)
    {
        var d = new float[w * h * 3];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                double t = y < h * 0.30 ? 0.05 : y > h * 0.85 ? 0.004 : 0.02;
                int i = (y * w + x) * 3;
                d[i] = (float)t; d[i + 1] = (float)t; d[i + 2] = (float)t;
            }
        return new ImageBuffer(w, h, d);
    }

    /// <summary>A frame with a flat, COLOURED bright band, so its bright-tail estimate is a known
    /// non-neutral triple — needed to tell a wholesale pick from a per-channel splice.</summary>
    private static ImageBuffer ColouredBrightBand(int w = 160, int h = 160)
    {
        var d = new float[w * h * 3];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                (double R, double G, double B) t = y < h * 0.35 ? (0.32, 0.69, 0.41)
                                                : y > h * 0.85 ? (0.004, 0.005, 0.006)
                                                               : (0.10, 0.20, 0.14);
                int i = (y * w + x) * 3;
                d[i] = (float)t.R; d[i + 1] = (float)t.G; d[i + 2] = (float)t.B;
            }
        return new ImageBuffer(w, h, d);
    }

    /// <summary>
    /// THE REGRESSION for the user's `t = 1.109`: no path may hand back a transmittance above 1.
    /// The input is the project's own numbers, on a frame dark enough that they win the selection —
    /// so removing the clamp returns them unchanged and this test fails.
    /// </summary>
    [Fact]
    public void WhiteEnd_NeverExceedsFullTransmission()
    {
        ImageBuffer img = DarkSlide();

        double[] impossible = { 0.5047, 1.1092, 0.6607 };
        double[] result = FilmBase.KeepWhiteEndAtBrightEnd(impossible, img);

        Assert.True(result.All(v => v <= 1.0), $"t_base must stay <= 1, got {Fmt(result)}");
        Assert.True(result.All(v => v > 0), $"t_base must stay > 0, got {Fmt(result)}");
        // D_min is what the picture consumes, and it may not go negative.
        Assert.True(result.Select(t => -Math.Log10(t)).All(v => v >= 0),
            $"D_min must be >= 0, got {Fmt(result.Select(t => -Math.Log10(t)).ToArray())}");
    }

    /// <summary>
    /// The clamp returns ONE of its two candidates, whole. The fixture makes the two candidates
    /// disagree in opposite directions (the base is brighter in R and B, the frame's bright tail in
    /// G), which is exactly the shape a per-channel max would splice into a third triple that
    /// belongs to no patch of film — the mechanism that fabricates a cast on a neutral base.
    /// </summary>
    [Fact]
    public void WhiteEnd_IsOneCoSitedTriple_NotAPerChannelMix()
    {
        ImageBuffer img = ColouredBrightBand();
        double[] bright = FilmBase.EstimateTBaseFromRoll(new[] { img });

        // Brighter than `bright` in R and B, darker in G — and the SUM still loses to the frame's
        // own bright tail, so the wholesale rule must pick `bright`.
        double[] candidate = { 0.50, 0.10, 0.66 };
        Assert.True(candidate[0] > bright[0] && candidate[2] > bright[2] && candidate[1] < bright[1],
            $"fixture must straddle the two triples, got bright={Fmt(bright)} candidate={Fmt(candidate)}");
        Assert.True(candidate.Sum() < bright.Sum(),
            $"fixture must let the frame's bright tail win outright, got bright={Fmt(bright)}");

        double[] result = FilmBase.KeepWhiteEndAtBrightEnd(candidate, img);

        // A per-channel max would have produced [0.50, bright.G, 0.66].
        double[] spliced = { candidate[0], bright[1], candidate[2] };
        Assert.False(result.Select(v => Math.Round(v, 4)).SequenceEqual(spliced.Select(v => Math.Round(v, 4))),
            $"result {Fmt(result)} is the per-channel splice {Fmt(spliced)}");
        Assert.Equal(bright.Select(v => Math.Round(v, 4)), result.Select(v => Math.Round(v, 4)));
    }

    /// <summary>
    /// Both invariants on the ROLL path — the one the user's project came from (35+ frames shared
    /// one endpoint set, i.e. 自动（整卷）).
    /// </summary>
    [Fact]
    public void WhiteEnd_RollPath_IsBoundedAndCoSited()
    {
        var frames = new[] { DarkSlide(), DarkSlide() };

        double[] rollBase = FilmBase.EstimateTBaseFromRoll(frames);
        Assert.True(rollBase.All(v => v <= 1.0 && v > 0), $"roll t_base {Fmt(rollBase)}");

        double[] impossible = { 0.5047, 1.1092, 0.6607 };
        double[] clamped = FilmBase.KeepWhiteEndAtBrightEnd(impossible, frames[0]);
        Assert.True(clamped.All(v => v <= 1.0 && v > 0), $"clamped roll t_base {Fmt(clamped)}");
        Assert.True(clamped.Select(t => -Math.Log10(t)).All(v => v >= 0), "roll D_min must be >= 0");
    }
}
