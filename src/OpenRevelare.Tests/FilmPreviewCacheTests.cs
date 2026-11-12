using OpenRevelare.ColorManagement;
using OpenRevelare.Core;
using OpenRevelare.Gui.Services;
using Xunit;

namespace OpenRevelare.Tests;

public sealed class FilmPreviewCacheTests
{
    [Fact]
    public void Foreground_preparation_uses_film_cache_but_background_and_cache_retirement_do_not_retain_it()
    {
        using var vm = new OpenRevelare.Gui.ViewModels.MainViewModel(() => 2L << 30);
        const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        var prepare = vm.GetType().GetMethod("PrepareDisplayData", flags)!;
        var cache = (FilmPreviewCache)vm.GetType().GetField("_filmPreviews", flags)!.GetValue(vm)!;
        var source = Source(); var p = Parameters();
        CachedPreviewData Prepare(bool foreground) => (CachedPreviewData)prepare.Invoke(vm,
            [source, p, ColorPipelineVersion.ManagedV2, CancellationToken.None, null, foreground])!;
        Prepare(true);
        p.ExposureEv = 0.5;
        var result = Prepare(true);
        using var cmm = new LittleCmsEngine();
        EqualRender(Pipeline.Render(source, p, ColorPipelineVersion.ManagedV2, cmm), result.Rendered);
        Assert.Equal(1, cache.Preparations);
        Assert.Equal(1, cache.Hits);
        Prepare(false);
        Assert.Equal(1, cache.Preparations);
        vm.GetType().GetMethod("ClearPreviewCaches", flags)!.Invoke(vm, null);
        Assert.Equal(0, cache.ResidentBytes);
    }

    [Theory]
    [InlineData(ColorPipelineVersion.LegacyV1, false, "sRGB", false)]
    [InlineData(ColorPipelineVersion.LegacyV1, true, "AdobeRGB", true)]
    [InlineData(ColorPipelineVersion.ManagedV2, false, "DisplayP3", true)]
    [InlineData(ColorPipelineVersion.ManagedV2, true, "sRGB", false)]
    [InlineData(ColorPipelineVersion.ManagedV2, false, "hdr", true)]
    [InlineData(ColorPipelineVersion.ManagedV2, true, "none", false)]
    [InlineData(ColorPipelineVersion.LegacyV1, false, "none", true)]
    public void Prepared_stage_is_bit_exact_and_does_not_change_source_or_previous_results(
        ColorPipelineVersion version, bool mono, string output, bool lut)
    {
        var source = Source();
        float[] original = (float[])source.Pixels.Data.Clone();
        FrameParams p = Parameters();
        p.Monochrome = mono;
        p.OutputSpace = output == "hdr" ? "sRGB" : output;
        p.HdrPeakNits = output == "hdr" ? 1000 : 0;
        p.OutputIntent = output == "none" ? OutputIntent.None : OutputIntent.Basic;
        p.PrintLut = lut ? ":kodak-2383" : "";
        using var cmm = new LittleCmsEngine();
        var film = Pipeline.PrepareFilm(source, p);
        var first = Pipeline.RenderPreparedFilm(film, p, version, cmm);
        EqualRender(Pipeline.Render(source, p, version, cmm), first);
        float[] before = (float[])first.Pixels.Data.Clone();
        foreach (var change in CreativeChanges())
        {
            var edited = p.Clone();
            change(edited);
            EqualRender(Pipeline.Render(source, edited, version, cmm),
                Pipeline.RenderPreparedFilm(film, edited, version, cmm));
        }
        Assert.Equal(original, source.Pixels.Data);
        Assert.Equal(before, first.Pixels.Data);
        // A caller can edit its returned buffer without poisoning later finishes.
        first.Pixels.Data[0] = 42;
        EqualRender(Pipeline.Render(source, p, version, cmm), Pipeline.RenderPreparedFilm(film, p, version, cmm));
    }

    [Theory]
    [InlineData(ColorPipelineVersion.LegacyV1)]
    [InlineData(ColorPipelineVersion.ManagedV2)]
    public void Creative_edits_reuse_film_but_match_full_recomputation(ColorPipelineVersion version)
    {
        var source = Source();
        var cache = new FilmPreviewCache(() => 2L << 30);
        var p = Parameters();
        using var cmm = new LittleCmsEngine();
        EqualRender(Pipeline.Render(source, p, version, cmm), cache.Render(source, p, version, cmm, default));
        foreach (var change in CreativeChanges())
        {
            var edited = p.Clone();
            change(edited);
            Assert.Equal(PreviewParametersKey.CreateFilm(p), PreviewParametersKey.CreateFilm(edited));
            EqualRender(Pipeline.Render(source, edited, version, cmm), cache.Render(source, edited, version, cmm, default));
        }
        Assert.Equal(1, cache.Preparations);
        Assert.Equal(CreativeChanges().Length, cache.Hits);
    }

    [Fact]
    public void Physical_geometry_dust_and_output_changes_invalidate_and_match_fresh_pixels()
    {
        Action<FrameParams>[] changes =
        [
            p => p.TBase[0] = 0.95, p => p.DMinPerChannel[1] = 0.05,
            p => p.DMaxPerChannel[2] = 2.4, p => p.Monochrome = true,
            p => p.ChromaChannelScale[0] = 1.1, p => p.UseC41Crosstalk = true,
            p => p.DecoupleMatrix = new double[,] { { 1, 0.01, 0 }, { 0, 1, 0 }, { 0, 0, 1 } },
            p => p.DecoupleChromaMatrix = new double[,] { { 0.9, 0.1, 0 }, { 0, 1, 0 }, { 0, 0, 1 } },
            p => p.DecoupleChromaAmp = [1.1, 1, 1],
            p => p.LccFlatField = new ImageBuffer(2, 2, Enumerable.Repeat(0.9f, 12).ToArray()),
            p => p.DistortionK1 = 0.02, p => p.VignetteAmount = 0.2,
            p => p.Rotation = 7, p => p.QuarterTurns = 2, p => p.FlipH = !p.FlipH,
            p => p.FlipV = true, p => p.CropRect = (0.2, 0.2, 0.6, 0.6),
            p => p.SprocketThreshold = 0.6,
            p => p.DustSpots.Add(new DustSpot(0.4, 0.4, 0.02)),
            p => p.AutomaticDustMask = new DustMask(64, 48, (0, 0, 1, 1), [new(12, 10, 13)]),
            p => p.EmbeddedLens = new EmbeddedLensCalibration("test", VignettePolynomial: [0.1]),
            p => p.OutputSpace = "DisplayP3", p => p.HdrPeakNits = 1000,
            p => p.PrintLut = ":kodak-2383", p => p.DisplayReferredStage2 = false,
        ];
        var source = Source();
        using var cmm = new LittleCmsEngine();
        foreach (var change in changes)
        {
            var cache = new FilmPreviewCache(() => 2L << 30);
            var p = Parameters();
            cache.Render(source, p, ColorPipelineVersion.ManagedV2, cmm, default);
            var edited = p.Clone();
            change(edited);
            Assert.NotEqual(PreviewParametersKey.CreateFilm(p), PreviewParametersKey.CreateFilm(edited));
            EqualRender(Pipeline.Render(source, edited, ColorPipelineVersion.ManagedV2, cmm),
                cache.Render(source, edited, ColorPipelineVersion.ManagedV2, cmm, default));
            Assert.Equal(2, cache.Preparations);
            Assert.Equal(0, cache.Hits);
        }
    }

    [Fact]
    public void Source_identity_pipeline_clear_and_memory_pressure_prevent_reuse()
    {
        long? free = 2L << 30;
        var cache = new FilmPreviewCache(() => free);
        using var cmm = new LittleCmsEngine();
        var p = Parameters();
        var source = Source();
        RenderedFrame Render(WorkingFrame s, ColorPipelineVersion v = ColorPipelineVersion.ManagedV2) => cache.Render(s, p, v, cmm, default);
        Render(source);
        Render(source.WithPixels(source.Pixels)); // different typed source admission identity
        Render(source, ColorPipelineVersion.LegacyV1);
        Assert.Equal(3, cache.Preparations);
        cache.Clear();
        Assert.Equal(0, cache.ResidentBytes);
        Render(source);
        free = 0;
        Render(source);
        Assert.Equal(0, cache.ResidentBytes);
        Render(source);
        Assert.Equal(6, cache.Preparations);
        free = 2L << 30;
        var disabled = new FilmPreviewCache(() => free, byteLimit: 1);
        disabled.Render(source, p, ColorPipelineVersion.ManagedV2, cmm, default);
        Assert.Equal(0, disabled.ResidentBytes);
        Assert.Equal(0, cache.Hits);
    }

    [Fact]
    public void Byte_and_entry_budgets_evict_old_sources()
    {
        var a = Source(); var b = Source(); var c = Source();
        var p = Parameters();
        using var cmm = new LittleCmsEngine();
        long bytes = Pipeline.PrepareFilm(a, p).ResidentBytes;
        var cache = new FilmPreviewCache(() => null, byteLimit: bytes * 2, countLimit: 2);
        foreach (var source in new[] { a, b, c, b }) cache.Render(source, p, ColorPipelineVersion.ManagedV2, cmm, default);
        Assert.Equal(3, cache.Preparations);
        Assert.Equal(1, cache.Hits);
        Assert.Equal(bytes * 2, cache.ResidentBytes);
        cache.Render(a, p, ColorPipelineVersion.ManagedV2, cmm, default);
        Assert.Equal(4, cache.Preparations);
        var one = new FilmPreviewCache(() => null, byteLimit: bytes);
        one.Render(a, p, ColorPipelineVersion.ManagedV2, cmm, default);
        one.Render(b, p, ColorPipelineVersion.ManagedV2, cmm, default);
        Assert.Equal(bytes, one.ResidentBytes);
    }

    [Fact]
    public void Changed_source_or_reference_is_rejected_even_when_film_would_hit()
    {
        string file = Path.Combine(TestDataIsolation.Root, $"film-source-{Guid.NewGuid():N}");
        string reference = file + ".flat";
        File.WriteAllText(file, "original"); File.WriteAllText(reference, "flat");
        try
        {
            var cache = new FilmPreviewCache(() => null);
            using var cmm = new LittleCmsEngine();
            var source = DecodedSources.Track(Source(), SourceStamp.Capture(file));
            var p = Parameters();
            p.LccFlatField = CalibrationSources.Track(new ImageBuffer(2, 2, Enumerable.Repeat(0.9f, 12).ToArray()),
                new SourceSnapshot([reference]));
            cache.Render(source, p, ColorPipelineVersion.ManagedV2, cmm, default);
            File.WriteAllText(reference, "changed reference");
            Assert.Throws<IOException>(() => cache.Render(source, p, ColorPipelineVersion.ManagedV2, cmm, default));
            p.LccFlatField = null;
            File.WriteAllText(file, "changed original");
            Assert.Throws<IOException>(() => cache.Render(source, p, ColorPipelineVersion.ManagedV2, cmm, default));
            Assert.Equal(1, cache.Preparations);
            Assert.Equal(0, cache.Hits);
        }
        finally { File.Delete(file); File.Delete(reference); }
    }

    [Fact]
    public void Cancellation_and_clear_during_preparation_do_not_repopulate_cache()
    {
        using var cmm = new LittleCmsEngine();
        var source = Source(); var p = Parameters();
        using var cts = new CancellationTokenSource();
        var cancelled = new FilmPreviewCache(() => { cts.Cancel(); return null; });
        Assert.Throws<OperationCanceledException>(() => cancelled.Render(source, p, ColorPipelineVersion.ManagedV2, cmm, cts.Token));
        Assert.Equal(0, cancelled.ResidentBytes);
        int calls = 0;
        FilmPreviewCache? cleared = null;
        cleared = new FilmPreviewCache(() => { if (++calls == 2) cleared!.Clear(); return null; });
        EqualRender(Pipeline.Render(source, p, ColorPipelineVersion.ManagedV2, cmm),
            cleared.Render(source, p, ColorPipelineVersion.ManagedV2, cmm, default));
        Assert.Equal(0, cleared.ResidentBytes);
    }

    private static Action<FrameParams>[] CreativeChanges() =>
    [
        p => p.WbGains[0] = 1.12, p => p.ExposureEv = 0.45,
        p => p.BlackPoint = 0.03, p => p.WhitePoint = 0.9, p => p.Contrast = 0.15,
        p => p.Highlights = -0.2, p => p.Shadows = 0.1, p => p.Saturation = 0.2,
        p => p.CurvePointsM = [(0, 0.02), (0.5, 0.55), (1, 0.99)],
        p => p.CurvePointsR = [(0, 0), (0.5, 0.6), (1, 1)],
        p => p.CurvePointsG = [(0, 0), (0.5, 0.4), (1, 1)],
        p => p.CurvePointsB = [(0, 0), (0.5, 0.6), (1, 1)],
        p => p.CurvePreserveHue = false, p => p.CurveHasEndpoints = true,
    ];

    private static FrameParams Parameters() => new()
    {
        Rotation = 3, QuarterTurns = 1, FlipH = true, CropRect = (0.04, 0.04, 0.92, 0.92),
        SprocketEnabled = true, SprocketThreshold = 0.9, DustEnabled = true,
        DustSpots = [new DustSpot(0.5, 0.5, 0.02)], VignetteAmount = 0.1,
    };

    private static WorkingFrame Source()
    {
        var pixels = new ImageBuffer(64, 48);
        var random = new Random(51);
        for (int i = 0; i < pixels.Data.Length; i++) pixels.Data[i] = 0.03f + 0.8f * random.NextSingle();
        for (int x = 0; x < pixels.Width; x++) for (int c = 0; c < 3; c++) pixels.Data[x * 3 + c] = 0.99f;
        var encoding = new UncharacterizedPixelEncoding(CaptureKind.Synthetic, "film-stage test",
            CompatibilityPolicy.LegacyTreatNumbersAsWorking, TransferState.Unknown, NumericRange.Extended);
        return new(pixels, WorkingSpaceId.LinearAcesCgV1, WorkingAdmission.LegacyUncharacterizedPassthrough,
            new SourceDescriptor("film-test", "film-test", encoding, "synthetic"));
    }

    private static void EqualRender(RenderedFrame expected, RenderedFrame actual)
    {
        Assert.Equal(expected.Pixels.Width, actual.Pixels.Width);
        Assert.Equal(expected.Pixels.Height, actual.Pixels.Height);
        Assert.True(System.Runtime.InteropServices.MemoryMarshal.AsBytes(expected.Pixels.Data.AsSpan()).SequenceEqual(
            System.Runtime.InteropServices.MemoryMarshal.AsBytes(actual.Pixels.Data.AsSpan())));
        Assert.Equal(expected.Encoding, actual.Encoding);
        Assert.Equal(expected.Recipe, actual.Recipe);
        Assert.Equal(expected.Fingerprint, actual.Fingerprint);
    }
}
