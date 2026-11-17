using System.Text;
using OpenRevelare.ColorManagement;
using OpenRevelare.Core;
using Xunit;

namespace OpenRevelare.Tests;

public sealed class CaptureWorkflowTests
{
    private static string NewDirectory()
    {
        string path = Path.Combine(TestDataIsolation.Root, "capture-flow-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path); return path;
    }
    private static DeviceCalibrationProfile Scanner => new()
    {
        Name = "Test input", DeviceModel = "Test scanner", Kind = CalibrationDeviceKind.Scanner,
        InputState = CaptureSignalState.LinearDeviceChannels,
        Scanner = new() { Workflow = ScannerCalibrationWorkflow.ArgyllCms, Mode = ScannerAcquisitionMode.Transmission },
    };

    [Fact]
    public void Camera_calibration_precedes_rgb_interpolation_and_orientation_without_clamping()
    {
        var meta = new CameraRawMosaicMetadata(4, 2, 4, 2, 0, 0, 0, "0,1,3,2",
            "Test", "Bayer", "unit", 100, .25, 4095, "session", "test");
        var dark = new CameraRawMosaic(Enumerable.Repeat((ushort)100, 8).ToArray(), meta, "dark.raw");
        var open = new CameraRawMosaic(Enumerable.Repeat((ushort)1100, 8).ToArray(), meta, "open.raw");
        // Native R=1.2, G=.4, B=.2 at every measured site.
        var sample = new CameraRawMosaic([1300, 500, 1300, 500, 500, 300, 500, 300], meta, "sample.raw");
        var measured = CameraCaptureCalibration.Measure(sample, dark, open);
        var rgb = CaptureWorkflow.ToBilinearRgb(measured);
        for (int i = 0; i < rgb.PixelCount; i++)
        {
            Assert.Equal(1.2f, rgb.Data[i * 3]); Assert.Equal(.4f, rgb.Data[i * 3 + 1]); Assert.Equal(.2f, rgb.Data[i * 3 + 2]);
        }
        // A spatially varying red site survives exactly, then rotates with the lattice.
        measured.Transmission[0] = .8f;
        var rotated = CaptureWorkflow.ToBilinearRgb(measured with { Metadata = meta with { Orientation = 6 } });
        Assert.Equal(2, rotated.Width); Assert.Equal(4, rotated.Height);
        Assert.Equal(.8f, rotated.Data[3]);
        measured.Transmission[1] = float.NaN;
        Assert.Throws<InvalidOperationException>(() => CaptureWorkflow.ToBilinearRgb(measured));
    }

    [Fact]
    public void Scanner_icc_workflow_creates_reopenable_float_input_and_persists_frozen_provenance()
    {
        string directory = NewDirectory();
        using var engine = new LittleCmsEngine();
        string icc = Path.Combine(directory, "scanner.icc"), source = Path.Combine(directory, "input.tif");
        // Matrix input fixture; class changed deliberately, not presented as a vendor profile.
        byte[] bytes = BuiltInColorProfiles.Srgb().IccBytes.ToArray(); Encoding.ASCII.GetBytes("scnr").CopyTo(bytes, 12);
        File.WriteAllBytes(icc, bytes);
        var input = new ImageBuffer(2, 2, Enumerable.Repeat(.5f, 12).ToArray()) { SourceQuantisationStep = 1.0 / 65535 };
        TiffIO.ExportCaptureIntermediate(input, source, false, "test untagged scanner device codes");
        byte[] original = File.ReadAllBytes(source);
        var profile = Scanner with { References = [DeviceCalibrationStore.Register(CalibrationReferenceRole.InputIcc, icc)] };
        var run = CaptureWorkflow.Prepare(profile, CaptureWorkflowMode.ScannerInputIcc, [source], Path.Combine(directory, "work"), engine);
        var output = Assert.Single(run.Sources);
        Assert.Equal(original, File.ReadAllBytes(source)); Assert.NotEqual(source, output.WorkingPath);
        Assert.Equal(64, output.WorkingSha256.Length);
        var reopened = TiffIO.LoadWorkingFrame(output.WorkingPath, TiffInputAssumption.Linear, ColorPipelineVersion.ManagedV2, engine);
        var expected = TiffIO.LoadWithScannerInputProfile(source,
            ColorProfileRef.Create(bytes, "test", ProfileRole.Input, new ProfileSource.Embedded("test", "fixture")), engine);
        Assert.Equal(expected.Pixels.Width, reopened.Pixels.Width);
        for (int i = 0; i < input.Data.Length; i++) Assert.InRange(Math.Abs(reopened.Pixels.Data[i] - expected.Pixels.Data[i]), 0, .00001f);
        Assert.InRange(Math.Abs(reopened.Pixels.SourceQuantisationStep - expected.Pixels.SourceQuantisationStep), 0, .000001);
        string projectPath = Path.Combine(directory, "workflow.ncproj");
        var data = new Project.Data { Meta = new() { CaptureWorkflow = run, TiffIsLinear = true } };
        data.Frames.Add(new() { SourcePath = output.WorkingPath });
        Project.Save(projectPath, data);
        File.Delete(icc); File.Delete(source); // Derived input is self-contained; re-open does not reapply references.
        var loaded = Project.Load(projectPath);
        Assert.Equal(run.Recipe, loaded.Meta.CaptureWorkflow!.Recipe);
        Assert.Equal(output.OriginalSha256, Assert.Single(loaded.Meta.CaptureWorkflow.Sources).OriginalSha256);
        Assert.Equal(WorkingAdmission.ConvertedFromCharacterized,
            TiffIO.LoadWorkingFrame(loaded.Frames[0].SourcePath, TiffInputAssumption.Linear, ColorPipelineVersion.ManagedV2, engine).Admission);
    }

    [Fact]
    public void Tagged_scanner_input_or_changed_reference_aborts_without_publishing_partial_batch()
    {
        string directory = NewDirectory(); using var engine = new LittleCmsEngine();
        string icc = Path.Combine(directory, "scanner.icc"), clean = Path.Combine(directory, "clean.tif"), tagged = Path.Combine(directory, "tagged.tif");
        byte[] bytes = BuiltInColorProfiles.Srgb().IccBytes.ToArray(); Encoding.ASCII.GetBytes("scnr").CopyTo(bytes, 12); File.WriteAllBytes(icc, bytes);
        var profile = Scanner with { References = [DeviceCalibrationStore.Register(CalibrationReferenceRole.InputIcc, icc)] };
        var image = new ImageBuffer(2, 2, Enumerable.Repeat(.5f, 12).ToArray());
        TiffIO.ExportCaptureIntermediate(image, clean, false, "fixture");
        TiffIO.ExportCaptureIntermediate(image, tagged, true, "already characterized");
        string work = Path.Combine(directory, "work");
        Assert.Throws<InvalidOperationException>(() => CaptureWorkflow.Prepare(profile, CaptureWorkflowMode.ScannerInputIcc, [clean, tagged], work, engine));
        Assert.Empty(Directory.GetDirectories(work)); Assert.True(File.Exists(clean)); Assert.True(File.Exists(tagged));
        File.WriteAllBytes(icc, new byte[40]);
        Assert.Throws<IOException>(() => CaptureWorkflow.Prepare(profile, CaptureWorkflowMode.ScannerInputIcc, [clean], work, engine));
    }

    [Fact]
    public void Record_only_preserves_inputs_without_generating_files_and_wrong_device_is_rejected()
    {
        string directory = NewDirectory(); using var engine = new LittleCmsEngine();
        string source = Path.Combine(directory, "scan.tif"), output = Path.Combine(directory, "unused");
        TiffIO.ExportCaptureIntermediate(new ImageBuffer(2, 2), source, false, "fixture");
        var run = CaptureWorkflow.Prepare(Scanner, CaptureWorkflowMode.RecordUpstream, [source], output, engine);
        Assert.False(Directory.Exists(output)); Assert.Equal(source, Assert.Single(run.Sources).WorkingPath);
        Assert.Throws<InvalidOperationException>(() => CaptureWorkflow.ValidateSelection(Scanner, CaptureWorkflowMode.CameraTransmission, true));
        Assert.Throws<InvalidOperationException>(() => CaptureWorkflow.ValidateSelection(Scanner with { InputState = CaptureSignalState.ColorManagedPixels }, CaptureWorkflowMode.ScannerInputIcc, false));
    }

    [Fact]
    public void Camera_float_intermediate_preserves_extended_values_and_source_lattice_on_preview_and_full_load()
    {
        string directory = NewDirectory(), path = Path.Combine(directory, "camera.tif");
        using var engine = new LittleCmsEngine();
        var input = new ImageBuffer(4, 4, Enumerable.Repeat(1.2f, 48).ToArray()) { SourceQuantisationStep = .001 };
        TiffIO.ExportCaptureIntermediate(input, path, false, "camera-relative-transmission");
        var full = TiffIO.LoadWorkingFrame(path, TiffInputAssumption.Linear, ColorPipelineVersion.ManagedV2, engine);
        var preview = TiffIO.LoadWorkingRegion(path, (0, 0, 1, 1), 2, TiffInputAssumption.Linear, ColorPipelineVersion.ManagedV2, engine);
        Assert.All(full.Pixels.Data, v => Assert.Equal(1.2f, v));
        Assert.All(preview.Pixels.Data, v => Assert.Equal(1.2f, v));
        Assert.Equal(.001, full.Pixels.SourceQuantisationStep, 7);
        Assert.Equal(.001, preview.Pixels.SourceQuantisationStep, 7);
        Assert.IsType<UncharacterizedPixelEncoding>(full.Source.OriginalEncoding);
    }
}
