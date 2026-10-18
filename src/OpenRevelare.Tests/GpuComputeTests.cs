using OpenRevelare.Core;
using Xunit;
using OpenRevelare.ColorManagement;
using OpenRevelare.Presentation;

namespace OpenRevelare.Tests;

// General CI may have no hardware GPU. Set OPENREVELARE_REQUIRE_GPU=1 for real-driver
// acceptance on Windows/macOS; initialization/compilation errors must then fail tests.
public sealed class GpuFactAttribute : FactAttribute
{
    public GpuFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("OPENREVELARE_REQUIRE_GPU") != "1")
            Skip = "Requires a hardware GPU; set OPENREVELARE_REQUIRE_GPU=1 to run.";
    }
}

[CollectionDefinition("GPU scheduler", DisableParallelization = true)]
public sealed class GpuSchedulerCollection;

[Collection("GPU scheduler")]
public sealed class GpuComputeTests
{
    [Fact]
    public void Automatic_route_retires_after_sustained_slowdown_but_tolerates_one_outlier()
    {
        var initial = new GpuWorkloadMeasurement("delivery",10,4,true);
        var oneSlowRun = initial.Observe(15);
        Assert.True(oneSlowRun.Profitable);
        Assert.True(oneSlowRun.Observe(5).Profitable);
        Assert.Equal(0,oneSlowRun.Observe(5).ConsecutiveSlowRuns);
        var retired = oneSlowRun.Observe(20);
        Assert.False(retired.Profitable);
        Assert.Equal(20d,retired.LastGpuMs);
        Assert.Equal(4,retired.GpuMs); // Retain the calibration for diagnosis.
    }

    [GpuFact]
    public void Print_lut_gpu_preserves_complete_managed_render_and_canonical_scene()
    {
        var previous = GpuAcceleration.Mode;
        try
        {
            using var cmm = new LittleCmsEngine();
            var pixels = Fixture(113,57);
            for (int i=0; i<pixels.Data.Length; i++) pixels.Data[i] = Math.Abs(pixels.Data[i]) + 0.001f;
            var encoding = new UncharacterizedPixelEncoding(CaptureKind.Synthetic,"test:gpu",
                CompatibilityPolicy.LegacyTreatNumbersAsWorking,TransferState.Unknown,NumericRange.Extended);
            var source = new WorkingFrame(pixels,WorkingSpaceId.LinearAcesCgV1,WorkingAdmission.LegacyUncharacterizedPassthrough,
                new SourceDescriptor("test:gpu","GPU parity fixture",encoding,"generated float32"));
            foreach (var stock in PrintLuts.Builtins)
                foreach (double peak in new[] { 0d,1000d })
                {
                    var parameters = new FrameParams { PrintLut=stock.Id, HdrPeakNits=peak,
                        ExposureEv=0.35,Contrast=0.12,Saturation=0.08,DisplayReferredStage2=true };
                    GpuAcceleration.Mode = GpuAccelerationMode.Off;
                    var expected = Pipeline.Render(source,parameters,ColorPipelineVersion.ManagedV2,cmm);
                    var expectedScene = CanonicalPreviewConverter.Convert(expected,cmm,1);
                    GpuAcceleration.Mode = GpuAccelerationMode.On;
                    var actual = Pipeline.Render(source,parameters,ColorPipelineVersion.ManagedV2,cmm);
                    var actualScene = CanonicalPreviewConverter.Convert(actual,cmm,1);
                    Assert.True(expected.Pixels.Data.AsSpan().SequenceEqual(actual.Pixels.Data),$"Render {stock.Id} / {peak}");
                    for (int i=0;i<expectedScene.LinearExtendedSrgbRgba.Length;i++)
                        Assert.True(expectedScene.LinearExtendedSrgbRgba[i].Equals(actualScene.LinearExtendedSrgbRgba[i]),
                            $"Scene {stock.Id} / {peak}, index={i}: CPU={expectedScene.LinearExtendedSrgbRgba[i]}, GPU={actualScene.LinearExtendedSrgbRgba[i]}");
                    Assert.Contains(GpuAcceleration.Measurements(),m => m.Workload.StartsWith("cube-lut:"));
                }
        }
        finally { GpuAcceleration.ReleaseResources(); GpuAcceleration.Mode=previous; }
    }

    [GpuFact]
    public void Resident_resize_sharpen_matches_separate_cpu_operations()
    {
        var previous = GpuAcceleration.Mode;
        try
        {
            GpuAcceleration.Mode = GpuAccelerationMode.Off;
            using var session = new GpuComputeSession();
            foreach (var (w, h, edge) in new[] { (257,129,113), (47,23,94), (17,17,9) })
            {
                var source = Fixture(w,h);
                source.Data[36] = float.NaN;
                var before = (float[])source.Data.Clone();
                var expected = Resample.ToLongEdge(source,edge,true);
                OutputSharpen.Apply(expected,OutputSharpen.Level.Standard);
                var actual = session.ResizeSharpen(source,expected.Width,expected.Height,Gaussian(0.7f),0.65f,2f/255f,0.12f);
                Assert.True(GpuAcceleration.Equivalent(expected.Data,actual));
                Assert.Equal(before,source.Data);
            }
        }
        finally { GpuAcceleration.Mode = previous; }
    }

    [Fact]
    public void Combined_delivery_preserves_source_and_handles_unchanged_or_tiny_sizes()
    {
        var previous = GpuAcceleration.Mode;
        try
        {
            GpuAcceleration.Mode = GpuAccelerationMode.Off;
            foreach (var (edge, level) in new[] { (94,OutputSharpen.Level.Standard), (47,OutputSharpen.Level.High),
                (1,OutputSharpen.Level.Standard), (23,OutputSharpen.Level.None) })
            {
                var source = Fixture(47,23);
                source.SourceQuantisationStep = 1.0 / 65535;
                var before = (float[])source.Data.Clone();
                var resized = Resample.ToLongEdge(source,edge,true);
                var expected = new ImageBuffer(resized.Width,resized.Height,(float[])resized.Data.Clone());
                OutputSharpen.Apply(expected,level);
                var actual = OutputSharpen.ResizeAndApply(source,edge,true,level);
                Assert.Equal(expected.Data,actual.Data);
                Assert.Equal(source.SourceQuantisationStep,actual.SourceQuantisationStep);
                Assert.Equal(before,source.Data);
            }
        }
        finally { GpuAcceleration.Mode = previous; }
    }

    [GpuFact]
    public void Gpu_cube_is_exact_for_real_stocks_domain_edges_and_dispatch_tail()
    {
        using var session = new GpuComputeSession();
        foreach (var stock in PrintLuts.Builtins)
        {
            var lut = PrintLuts.Resolve(stock.Id)!;
            var source = Fixture(47, 23).Data;
            source[0] = 0; source[1] = 1f; source[2] = float.PositiveInfinity;
            source[3] = float.NegativeInfinity; source[4] = float.Epsilon; source[5] = 1f;
            float[] before = (float[])source.Clone();
            var expected = (float[])source.Clone();
            lut.ApplyCpu(expected);
            float[] actual = session.CubeLut(source, lut.Table, lut.Size, lut.GpuParameters());
            Assert.Equal(expected, actual);
            Assert.Equal(before, source);
        }
    }

    [GpuFact]
    public void Tiled_sharpen_matches_cpu_at_seams_boundaries_and_nonfinite_neighbourhoods()
    {
        using var session = new GpuComputeSession();
        foreach (int rows in new[] { 1, 2, 7, 23, 64 })
        {
            var source = Fixture(47, 23);
            Array.Fill(source.Data, 1f, 0, 47 * 3);
            source.Data[7 * 47 * 3 + 9] = float.NaN;
            source.Data[14 * 47 * 3 + 10] = float.PositiveInfinity;
            float[] before = (float[])source.Data.Clone();
            float[] actual = new float[source.Data.Length];
            session.SharpenTiles(source, Gaussian(0.9f), 1f, 2f / 255f, 0.12f, actual, rows);
            Assert.Equal(before, source.Data);
            OutputSharpen.Apply(source, OutputSharpen.Level.High);
            Assert.True(GpuAcceleration.Equivalent(source.Data, actual), $"Strip height {rows}");
        }
    }

    [GpuFact]
    public void Scheduler_uses_gpu_again_after_resource_fallback_without_caching_failed_calibration()
    {
        var previous = GpuAcceleration.Mode;
        try
        {
            GpuAcceleration.Mode = GpuAccelerationMode.Off;
            var small = Fixture(47, 23);
            float[] expected = Resample.ToLongEdge(small, 94, true).Data;
            GpuAcceleration.Mode = GpuAccelerationMode.On;
            var source = Fixture(1, 65536);
            Assert.False(GpuAcceleration.TryResize(source, 65536, 1,
                () => throw new Exception("Rejected workload must not calibrate"), out _));
            Assert.Empty(GpuAcceleration.Measurements());
            Assert.True(GpuAcceleration.TryResize(small, 94, 46, () => expected, out var actual));
            Assert.True(GpuAcceleration.Equivalent(expected, actual));
            Assert.Single(GpuAcceleration.Measurements());
        }
        finally
        {
            GpuAcceleration.ReleaseResources();
            GpuAcceleration.Mode = previous;
        }
    }

    [GpuFact]
    public void Oversized_intermediate_is_rejected_before_allocation_and_device_remains_usable()
    {
        using var session = new GpuComputeSession();
        // Small input/output, but the horizontal intermediate would require 48 GiB.
        // This also exceeds D3D's per-buffer limit, independent of installed VRAM.
        var source = Fixture(1, 65536);
        Assert.Throws<GpuResourceUnavailableException>(() => session.Resize(source, 65536, 1));
        var small = Fixture(47, 23);
        Assert.True(GpuAcceleration.Equivalent(Resample.ToLongEdge(small, 94, true).Data,
            session.Resize(small, 94, 46)));
    }

    [GpuFact]
    public void Gpu_resample_matches_cpu_for_area_enlargement_borders_and_single_pixel_axes()
    {
        using var session = new GpuComputeSession();
        foreach (var (w, h, edge) in new[] { (257, 129, 113), (47, 23, 83), (1, 7, 13), (17, 1, 9), (5, 5, 1) })
        {
            var source = Fixture(w, h);
            var (dw, dh) = Resample.LongEdgeSize(w, h, edge, true);
            float[] before = (float[])source.Data.Clone();
            float[] actual = session.Resize(source, dw, dh);
            // Small fixtures always exercise the CPU path under the default Auto policy.
            float[] reference = Resample.ToLongEdge(source, edge, true).Data;
            Assert.True(GpuAcceleration.Equivalent(reference, actual), $"Mismatch at {w}x{h} -> {dw}x{dh}");
            Assert.Equal(before, source.Data);
        }
    }

    [GpuFact]
    public void Gpu_sharpen_matches_cpu_including_threshold_white_fill_and_nonfinite_samples()
    {
        using var session = new GpuComputeSession();
        foreach (var (sigma, amount, level) in new[] {
            (0.5f, 0.35f, OutputSharpen.Level.Low), (0.7f, 0.65f, OutputSharpen.Level.Standard),
            (0.9f, 1f, OutputSharpen.Level.High) })
        {
            var source = Fixture(257, 129);
            // Exercise white fill, finite HDR/negative values and a nonfinite neighbourhood.
            Array.Fill(source.Data, 1f, 0, 257 * 3);
            source.Data[257 * 3 + 15] = float.NaN;
            float[] before = (float[])source.Data.Clone();
            float[] actual = session.Sharpen(source, Gaussian(sigma), amount, 2f / 255f, 0.12f);
            Assert.Equal(before, source.Data);
            OutputSharpen.Apply(source, level);
            Assert.True(GpuAcceleration.Equivalent(source.Data, actual));
        }
    }

    [GpuFact]
    public void Device_buffers_can_grow_and_shrink_between_operations()
    {
        using var session = new GpuComputeSession();
        foreach (int edge in new[] { 129, 513, 65, 257 })
        {
            var source = Fixture(edge, edge / 2);
            float[] resized = session.Resize(source, edge / 2, edge / 4);
            Assert.All(resized, sample => Assert.True(float.IsFinite(sample)));
            float[] sharpened = session.Sharpen(source, Gaussian(0.7f), 0.65f, 2f / 255f, 0.12f);
            var expected = Fixture(edge, edge / 2);
            OutputSharpen.Apply(expected, OutputSharpen.Level.Standard);
            Assert.True(GpuAcceleration.Equivalent(expected.Data, sharpened));
        }
    }

    [Fact]
    public void Parity_check_rejects_corruption_and_accepts_equivalent_nonfinite_values()
    {
        Assert.True(GpuAcceleration.Equivalent([0f, -0.1f, 4f, float.NaN, float.PositiveInfinity],
            [0f, -0.1f, 4f, float.NaN, float.PositiveInfinity]));
        Assert.False(GpuAcceleration.Equivalent([0f], [float.NaN]));
        Assert.False(GpuAcceleration.Equivalent([float.PositiveInfinity], [float.NegativeInfinity]));
        Assert.False(GpuAcceleration.Equivalent([0.5f], [0.51f]));
        Assert.False(GpuAcceleration.Equivalent([0f], [0f, 1f]));
    }

    private static ImageBuffer Fixture(int width, int height)
    {
        var image = new ImageBuffer(width, height);
        var random = new Random(42);
        for (int i = 0; i < image.Data.Length; i++) image.Data[i] = random.NextSingle() * 1.4f - 0.1f;
        return image;
    }

    private static float[] Gaussian(float sigma)
    {
        int radius = (int)MathF.Ceiling(sigma * 3f);
        var result = new float[radius * 2 + 1];
        float sum = 0;
        for (int i = -radius; i <= radius; i++)
        {
            result[i + radius] = MathF.Exp(-(i * i) / (2f * sigma * sigma));
            sum += result[i + radius];
        }
        for (int i = 0; i < result.Length; i++) result[i] /= sum;
        return result;
    }
}
