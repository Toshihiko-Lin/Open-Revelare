using BitMiracle.LibTiff.Classic;
using OpenRevelare.Core;
using Xunit;

namespace OpenRevelare.Tests;

public sealed class ScannerCaptureCalibrationTests
{
    private static ImageBuffer Image(params double[] densities) => new(densities.Length, 1,
        densities.SelectMany(d => Enumerable.Repeat((float)Math.Pow(10, -d), 3)).ToArray());
    private static ScannerReferenceSet Reference() => new()
    {
        IsTemplate = false, IndependentValidationCaptureDeclared = true,
        TargetIdentifier = "measured-test", ReferenceDefinition = "synthetic density reference",
        MaterialScope = "synthetic neutral material only", ReferenceMeasurementConditions = "known mathematical fixture",
        MaximumValidationDensityError = .00001,
        Patches = new[] { .2, .6, 1.0, .4, .8 }.Select((d, i) =>
            new ScannerReferencePatch(i < 3 ? i : i - 3, 0, 1, 1, Enumerable.Repeat(2 * d + .1, 3).ToArray(), i >= 3)).ToArray(),
    };

    [Fact]
    public void Known_density_response_is_recovered_and_applied_only_within_the_measured_range()
    {
        var train = Image(.2, .6, 1); var validation = Image(.4, .8);
        var before = (float[])train.Data.Clone();
        var model = ScannerCaptureCalibration.Fit(train, validation, Reference());
        Assert.All(model.Scale, s => Assert.Equal(2, s, 5));
        Assert.All(model.Offset, b => Assert.Equal(.1, b, 5));
        Assert.All(model.ValidationMaximumError, e => Assert.InRange(e, 0, .00001));
        var result = ScannerCaptureCalibration.Apply(Image(.4, 2), model);
        Assert.Equal(.9f, result.Density[0], 5);
        Assert.All(result.ValidChannels.Take(3), Assert.True);
        Assert.All(result.ValidChannels.Skip(3), Assert.False);
        Assert.All(result.Density.Skip(3), d => Assert.True(float.IsNaN(d)));
        Assert.Equal(before, train.Data);
    }

    [Fact]
    public void Templates_missing_reference_domains_and_nonindependent_validation_cannot_create_models()
    {
        var train = Image(.2, .6, 1); var check = Image(.4, .8); var r = Reference();
        Assert.Throws<ArgumentException>(() => ScannerCaptureCalibration.Fit(train, check, r with { IsTemplate = true }));
        Assert.Throws<ArgumentException>(() => ScannerCaptureCalibration.Fit(train, check, r with { ReferenceDefinition = "" }));
        Assert.Throws<ArgumentException>(() => ScannerCaptureCalibration.Fit(train, check, r with { IndependentValidationCaptureDeclared = false }));
        Assert.Throws<ArgumentException>(() => ScannerCaptureCalibration.Fit(train, check, r with { MaximumValidationDensityError = 0 }));
    }

    [Fact]
    public void Failed_independent_validation_and_overlapping_regions_reject_the_fit()
    {
        var r = Reference(); var bad = r.Patches.ToArray();
        bad[3] = bad[3] with { ReferenceDensity = [2, 2, 2] };
        Assert.Throws<InvalidOperationException>(() => ScannerCaptureCalibration.Fit(Image(.2, .6, 1), Image(.4, .8), r with { Patches = bad }));
        var overlap = r.Patches.ToArray(); overlap[1] = overlap[1] with { X = 0 };
        Assert.Throws<ArgumentException>(() => ScannerCaptureCalibration.Fit(Image(.2, .6, 1), Image(.4, .8), r with { Patches = overlap }));
        Assert.Throws<InvalidOperationException>(() => ScannerCaptureCalibration.Fit(Image(.2, .6, 1), Image(.01, .8), r));
        Assert.Throws<ArgumentException>(() => ScannerCaptureCalibration.Fit(Image(.6, .6, .6), Image(.4, .8), r));
    }

    [Fact]
    public void Device_tiff_reader_preserves_codes_and_refuses_colour_managed_input()
    {
        string path = Path.Combine(TestDataIsolation.Root, "scanner-codes-" + Guid.NewGuid().ToString("N") + ".tif");
        try
        {
            Write(path, icc: false);
            var image = TiffIO.LoadScannerMeasurementTiff(path);
            Assert.Equal(new float[] { 0, 1000 / 65535f, 1, 2 / 65535f, 2000 / 65535f, 3000 / 65535f }, image.Data);
            Write(path, icc: true);
            Assert.Throws<NotSupportedException>(() => TiffIO.LoadScannerMeasurementTiff(path));
        }
        finally { File.Delete(path); }
    }
    private static void Write(string path, bool icc)
    {
        using var tif = Tiff.Open(path, "w");
        tif.SetField(TiffTag.IMAGEWIDTH, 2); tif.SetField(TiffTag.IMAGELENGTH, 1);
        tif.SetField(TiffTag.SAMPLESPERPIXEL, 3); tif.SetField(TiffTag.BITSPERSAMPLE, 16);
        tif.SetField(TiffTag.PHOTOMETRIC, Photometric.RGB); tif.SetField(TiffTag.PLANARCONFIG, PlanarConfig.CONTIG);
        tif.SetField(TiffTag.ROWSPERSTRIP, 1); tif.SetField(TiffTag.SAMPLEFORMAT, SampleFormat.UINT);
        if (icc) tif.SetField(TiffTag.ICCPROFILE, 4, new byte[] { 1, 2, 3, 4 });
        ushort[] codes = [0, 1000, 65535, 2, 2000, 3000];
        byte[] row = new byte[12]; Buffer.BlockCopy(codes, 0, row, 0, row.Length);
        Assert.True(tif.WriteScanline(row, 0));
    }
}
