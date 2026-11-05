using OpenRevelare.Core;
using Xunit;

namespace OpenRevelare.Tests;

public sealed class LensfunCorrectionTests
{
    internal static LensfunCalibration Calibration(bool distortion = true, bool vignette = true) => new(
        "Test macro", "Test body", 50, 8, 1, 0.5, 0, 0,
        distortion ? new("ptlens", 0.015, -0.04, 0.01, 1, 1.5) : null,
        vignette ? new(-0.2, 0.04, 0, 1) : null);

    private static ImageBuffer Source(int w = 121, int h = 81)
    {
        var image = new ImageBuffer(w, h);
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        for (int c = 0; c < 3; c++) image.Data[(y * w + x) * 3 + c] = 0.1f + x * 0.001f + y * 0.002f + c * 0.03f;
        return image;
    }

    [Theory]
    [InlineData("poly3", 0.1, 0, 0, 0.5, 0.925)]
    [InlineData("poly5", 0.1, -0.02, 0, 0.5, 1.02375)]
    [InlineData("ptlens", 0.02, -0.05, 0.01, 0.5, 1.015)]
    public void Distortion_models_follow_documented_radius_equations(string model, double a, double b, double c,
        double radius, double expected)
        => Assert.Equal(expected, LensfunCorrections.DistortionScale(new(model, a, b, c, 1, 1.5), radius), 10);

    [Fact]
    public void Disabled_profile_is_a_true_pass_through_and_enabled_profile_preserves_source()
    {
        var source = Source(); var frozen = (float[])source.Data.Clone();
        var parameters = new FrameParams { Lensfun = Calibration() };
        Assert.Same(source, LensfunCorrections.Apply(source, parameters));
        parameters.LensfunEnabled = true;
        var corrected = LensfunCorrections.Apply(source, parameters);
        Assert.Equal(frozen, source.Data);
        Assert.NotEqual(frozen[0], corrected.Data[0]);
    }

    [Fact]
    public void Profile_vignetting_uses_calibration_sensor_diagonal_and_crop_factor()
    {
        var image = new ImageBuffer(51, 51, Enumerable.Repeat(0.2f, 51 * 51 * 3).ToArray());
        var calibration = Calibration(distortion: false) with { Vignetting = new(-0.4, 0, 0, 1) };
        LensfunCorrections.ApplyVignetting(image, calibration);
        double r2 = Math.Pow(50d / 51, 2);
        Assert.Equal(0.2 / (1 - 0.4 * r2), image.Data[0], 6);
        Assert.Equal(0.2f, image.Data[(25 * 51 + 25) * 3]);
    }

    [Fact]
    public void Lcc_suppresses_profile_vignetting_and_is_applied_before_distortion()
    {
        var source = Source();
        var flat = new ImageBuffer(source.Width, source.Height, Enumerable.Repeat(0.8f, source.Data.Length).ToArray());
        var parameters = new FrameParams { Lensfun = Calibration(), LensfunEnabled = true, LccFlatField = flat };
        var actual = LensfunCorrections.Apply(source, parameters);
        var onlyDistortion = parameters.Clone();
        onlyDistortion.Lensfun = Calibration(vignette: false);
        Assert.Equal(LensfunCorrections.Apply(source, onlyDistortion).Data, actual.Data);
        var preCorrected = new ImageBuffer(source.Width, source.Height, (float[])source.Data.Clone());
        Lcc.Apply(preCorrected.Data, source.Width, source.Height, flat);
        onlyDistortion.LccFlatField = null;
        Assert.Equal(LensfunCorrections.Apply(preCorrected, onlyDistortion).Data, actual.Data);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Pipeline_applies_profile_once_with_or_without_lcc(bool lcc)
    {
        var source = Source();
        var parameters = new FrameParams { Lensfun = Calibration(), LensfunEnabled = true, OutputIntent = OutputIntent.None };
        if (lcc) parameters.LccFlatField = new ImageBuffer(121, 81, Enumerable.Repeat(0.8f, source.Data.Length).ToArray());
        var actual = Pipeline.ProcessFrame(source, parameters);
        var corrected = LensfunCorrections.Apply(source, parameters);
        var rest = parameters.Clone(); rest.LensfunEnabled = false; rest.LccFlatField = null;
        Assert.Equal(Pipeline.ProcessFrame(corrected, rest).Data, actual.Data);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Regional_correction_matches_whole_frame_with_combined_manual_distortion(bool withLcc)
    {
        var source = Source();
        var parameters = new FrameParams { Lensfun = Calibration(), LensfunEnabled = true,
            DistortionK1 = -0.015, VignetteAmount = 0.03, OutputIntent = OutputIntent.None };
        if (withLcc) parameters.LccFlatField = new ImageBuffer(121, 81, Enumerable.Repeat(0.9f, source.Data.Length).ToArray());
        var roi = new RegionRender.Roi(0.23, 0.21, 0.31, 0.36);
        var full = Pipeline.ProcessFrame(source, parameters);
        var patch = RegionRender.Render(source, parameters, roi);
        int left = (int)Math.Round(patch.Realised.X * full.Width), top = (int)Math.Round(patch.Realised.Y * full.Height);
        for (int y = 0; y < patch.Image.Height; y++)
        for (int x = 0; x < patch.Image.Width; x++)
        for (int c = 0; c < 3; c++) Assert.InRange(Math.Abs(patch.Image.Data[(y * patch.Image.Width + x) * 3 + c]
            - full.Data[((y + top) * full.Width + x + left) * 3 + c]), 0, 2e-6);
        var bounds = RegionRender.RequiredSourceBounds(121, 81, parameters, roi);
        Assert.True((bounds.X1 - bounds.X0) * (bounds.Y1 - bounds.Y0) < source.PixelCount);
    }

    [Fact]
    public void Calibration_and_disabled_choice_survive_project_round_trip()
    {
        string path = Path.Combine(TestDataIsolation.Root, $"lensfun-{Guid.NewGuid():N}.ncproj");
        try
        {
            Project.Save(path, new Project.Data { Frames = new() { new() { SourcePath = "frame.dng",
                Params = new() { Lensfun = Calibration(), LensfunEnabled = false } } } });
            var loaded = Project.Load(path).Frames[0].Params;
            Assert.Equal(Calibration(), loaded.Lensfun);
            Assert.False(loaded.LensfunEnabled);
            loaded.LensfunEnabled = true;
            Assert.True(loaded.Clone().LensfunEnabled);
            Project.Save(path, new Project.Data { Frames = new() { new() { SourcePath = "frame.dng", Params = loaded } } });
            Assert.True(Project.Load(path).Frames[0].Params.LensfunEnabled);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Bundled_profile_produces_usable_first_file_calibration()
    {
        var db = LensfunIdentificationDatabase.Bundled.Value;
        var capture = new LensCaptureInfo("Panasonic", "DMC-G1", "Lumix G 14mm f/2.5 Asph.", 14, 4);
        var match = db.Identify(capture);
        var calibration = db.CreateCalibration(match.Profile!, capture);
        Assert.NotNull(calibration);
        Assert.True(calibration.IsUsable);
        Assert.Equal(2, calibration.CameraCropFactor);
        Assert.NotNull(calibration.Distortion);
        Assert.NotNull(calibration.Vignetting);
        Assert.Null(db.CreateCalibration(match.Profile!, capture with { Camera = "Unknown body" }));
        Assert.Equal(14, db.CreateCalibration(match.Profile!, capture with { FocalLength = null })!.FocalLength);
        Assert.NotNull(db.CreateCalibration(match.Profile!, capture with { Aperture = null })!.Distortion);
    }
}
