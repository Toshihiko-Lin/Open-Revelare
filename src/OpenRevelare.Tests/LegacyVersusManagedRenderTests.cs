using OpenRevelare.ColorManagement;
using OpenRevelare.Core;
using Xunit;

namespace OpenRevelare.Tests;

/// <summary>
/// Where LegacyV1 and ManagedV2 agree, and where they do not.
///
/// <para>
/// The migration dialog tells the user "画面和之后的导出可能变化", which is true but says nothing
/// about WHICH projects change or by how much — and that is the question anyone deciding whether to
/// migrate a finished roll actually has. These tests pin the answer so it cannot drift silently:
/// the boundary is a promise to users, not an implementation detail.
/// </para>
///
/// <para>
/// The three configurations that differ are NOT equivalent in character:
/// two of them are cases where v1's own <see cref="OutputRecipe.PixelProfileMismatch"/> is already
/// true — v1 knew it was labelling pixels with a profile that did not describe them — while the
/// curve case reports no mismatch at all. That one is the private gamma round-trip Stage 2's
/// comment claimed to have removed and had not, so v1 is SILENTLY different there. A user
/// migrating a roll with tone curves gets a visible change that nothing in v1 flagged.
/// </para>
/// </summary>
public sealed class LegacyVersusManagedRenderTests
{
    /// <summary>
    /// The ordinary case is BIT-IDENTICAL, and that is the load-bearing fact: migrating a plain
    /// roll is a no-op for the picture, so the dialog's warning is about the minority of projects.
    /// Adjustments ride along unchanged because they all run before the output boundary.
    /// </summary>
    [Theory]
    [InlineData("sRGB")]
    [InlineData("DisplayP3")]
    [InlineData("AdobeRGB")]
    public void No_print_lut_and_no_curves_renders_identically(string outputSpace)
    {
        (float[] v1, float[] v2) = RenderBoth(() => new FrameParams
        {
            OutputSpace = outputSpace,
            PrintLut = "",
            DisplayReferredStage2 = true,
            WbGains = new[] { 1.04, 0.98, 1.02 },
            ExposureEv = 0.2,
            BlackPoint = 0.01,
            WhitePoint = 0.96,
            Contrast = 0.08,
        });

        Assert.Equal(v1, v2);
    }

    /// <summary>
    /// `display_referred_stage2` is ABSENT from projects written before the field existed, and
    /// <c>Project.Load</c> reads a missing key as false — so this is the state old rolls are in,
    /// and it is by far the largest change migration makes. v1 leaves the pixels in ACEScg
    /// primaries while labelling them sRGB, which is precisely the I1 violation this PR exists to
    /// remove; the size of the delta is the size of that mislabelling.
    /// </summary>
    [Fact]
    public void Legacy_display_referred_false_changes_substantially()
    {
        (float[] v1, float[] v2) = RenderBoth(() => new FrameParams
        {
            OutputSpace = "sRGB",
            PrintLut = "",
            DisplayReferredStage2 = false,
        });

        Assert.True(MaxDelta(v1, v2) > 0.25, $"expected a large change, got {MaxDelta(v1, v2):F4}");
        Assert.True(LegacyRecipe(false, "").PixelProfileMismatch, "v1 should already admit the mismatch");
    }

    /// <summary>
    /// A print LUT changes because v1 kept the cube's Rec709 code values and merely attached the
    /// selected profile, where v2 converts them. v1 admits this one too.
    /// </summary>
    [Theory]
    [InlineData("sRGB")]
    [InlineData("DisplayP3")]
    public void A_print_lut_changes_and_v1_already_admitted_the_mismatch(string outputSpace)
    {
        (float[] v1, float[] v2) = RenderBoth(() => new FrameParams
        {
            OutputSpace = outputSpace,
            PrintLut = ":kodak-2383",
            DisplayReferredStage2 = true,
        });

        Assert.True(MaxDelta(v1, v2) > 0.01, $"expected a visible change, got {MaxDelta(v1, v2):F4}");
        Assert.True(LegacyRecipe(true, ":kodak-2383").PixelProfileMismatch);
    }

    /// <summary>
    /// THE ONE TO WATCH. Curves change the picture while v1 reports no mismatch whatsoever, so
    /// this difference is invisible to every diagnostic a user could consult before migrating.
    /// It is the <c>curvesAlreadyEncoded</c> correction: v1 ran the curve through a private
    /// gamma 2.2 round-trip that its own comment claimed had been removed.
    /// </summary>
    [Fact]
    public void A_tone_curve_changes_even_though_v1_reports_no_mismatch()
    {
        (float[] v1, float[] v2) = RenderBoth(() => new FrameParams
        {
            OutputSpace = "sRGB",
            PrintLut = "",
            DisplayReferredStage2 = true,
            CurvePointsM = new List<(double, double)> { (0, 0), (0.25, 0.18), (0.75, 0.82), (1, 1) },
        });

        Assert.True(MaxDelta(v1, v2) > 0.05, $"expected a visible change, got {MaxDelta(v1, v2):F4}");
        // The point of the test: nothing warned about it.
        Assert.False(LegacyRecipe(true, "").PixelProfileMismatch);
    }

    private static OutputRecipe LegacyRecipe(bool displayReferred, string printLut)
    {
        var cal = new FrameParams
        {
            OutputSpace = "sRGB",
            PrintLut = printLut,
            DisplayReferredStage2 = displayReferred,
        };
        using var engine = new LittleCmsEngine();
        return Pipeline.Render(Frame(), cal, ColorPipelineVersion.LegacyV1, engine).Recipe;
    }

    private static (float[] V1, float[] V2) RenderBoth(Func<FrameParams> make)
    {
        using var e1 = new LittleCmsEngine();
        using var e2 = new LittleCmsEngine();
        return (
            Pipeline.Render(Frame(), make(), ColorPipelineVersion.LegacyV1, e1).Pixels.Data,
            Pipeline.Render(Frame(), make(), ColorPipelineVersion.ManagedV2, e2).Pixels.Data);
    }

    private static double MaxDelta(float[] a, float[] b)
    {
        double max = 0;
        for (int i = 0; i < a.Length; i++) max = Math.Max(max, Math.Abs(a[i] - b[i]));
        return max;
    }

    /// <summary>A negative-ish spread: orange base through to dense highlights, plus saturated
    /// corners so a primaries change shows up rather than hiding on the neutral axis.</summary>
    private static WorkingFrame Frame()
    {
        var data = new List<float>();
        for (int i = 0; i < 24; i++)
        {
            float t = i / 23f;
            data.AddRange(new[] { 0.85f - 0.75f * t, 0.55f - 0.50f * t, 0.30f - 0.28f * t });
        }
        data.AddRange(new[]
        {
            0.90f, 0.20f, 0.10f,   0.15f, 0.80f, 0.20f,   0.10f, 0.20f, 0.85f,
            0.95f, 0.95f, 0.90f,   0.02f, 0.02f, 0.02f,
        });

        var pixels = new ImageBuffer(data.Count / 3, 1, data.ToArray());
        return new WorkingFrame(
            pixels,
            WorkingSpaceId.LinearAcesCgV1,
            WorkingAdmission.LegacyUncharacterizedPassthrough,
            new SourceDescriptor(
                "test:v1-vs-v2",
                "v1/v2 comparison synthetic negative",
                new UncharacterizedPixelEncoding(
                    CaptureKind.Synthetic,
                    "test:v1-vs-v2",
                    CompatibilityPolicy.LegacyTreatNumbersAsWorking,
                    TransferState.Unknown,
                    NumericRange.Extended),
                "test-generated RGB float32"));
    }
}
