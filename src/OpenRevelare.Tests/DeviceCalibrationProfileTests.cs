using OpenRevelare.Core;
using Xunit;

namespace OpenRevelare.Tests;

public sealed class DeviceCalibrationProfileTests
{
    [Fact]
    public void Draft_roundtrip_preserves_device_conditions_target_identity_and_reference_digest()
    {
        string root = NewRoot();
        string reference = Path.Combine(root, "target.txt"); File.WriteAllText(reference, "batch-specific reference");
        var profile = new DeviceCalibrationProfile
        {
            Name = "Scanner transparency", Kind = CalibrationDeviceKind.Scanner, DeviceModel = "Test scanner",
            DeviceSerial = "unit-1", CaptureConditions = "VueScan; transparency; fixed exposure",
            TargetIdentifier = "target-batch-123", References = [DeviceCalibrationStore.Register(CalibrationReferenceRole.TargetData, reference)],
            MeasurementTarget = CaptureMeasurementTarget.StandardDensity,
            InputState = CaptureSignalState.LinearDeviceChannels,
            MeasurementDefinition = "Independent measured density reference",
            LightSource = "Transmission unit A", DecodeRecipe = "Fixed exposure, linear export",
            AlreadyAppliedCorrections = "Driver black correction",
            Scanner = new() { Software = "VueScan", Mode = ScannerAcquisitionMode.Transmission,
                BlackCorrection = UpstreamCorrectionState.Applied, SpatialCorrection = UpstreamCorrectionState.Unknown,
                Workflow = ScannerCalibrationWorkflow.VueScanIt8 },
            Camera = new() { Lens = "Macro", Iso = "100", Exposure = "1/4 s" },
        };
        var store = new DeviceCalibrationStore(Path.Combine(root, "profiles"));
        store.Save(profile);
        var loaded = Assert.Single(store.LoadAll());
        Assert.Equal(profile.TargetIdentifier, loaded.TargetIdentifier);
        Assert.Equal(profile.CaptureConditions, loaded.CaptureConditions);
        Assert.Equal(profile.References, loaded.References);
        Assert.Equal(profile.MeasurementTarget, loaded.MeasurementTarget);
        Assert.Equal(profile.InputState, loaded.InputState);
        Assert.Equal(profile.MeasurementDefinition, loaded.MeasurementDefinition);
        Assert.Equal(profile.LightSource, loaded.LightSource);
        Assert.Equal(profile.DecodeRecipe, loaded.DecodeRecipe);
        Assert.Equal(profile.AlreadyAppliedCorrections, loaded.AlreadyAppliedCorrections);
        Assert.Equal(profile.Camera, loaded.Camera);
        Assert.Equal(profile.Scanner, loaded.Scanner);
        Assert.Empty(DeviceCalibrationStore.CheckReferences(loaded));
        File.WriteAllText(reference, "changed reference");
        Assert.Equal(reference, Assert.Single(DeviceCalibrationStore.CheckReferences(loaded)));
        File.Delete(reference);
        Assert.Equal(reference, Assert.Single(DeviceCalibrationStore.CheckReferences(loaded)));
    }

    [Fact]
    public void Unsupported_or_unsafe_profiles_cannot_escape_the_store()
    {
        var store = new DeviceCalibrationStore(Path.Combine(NewRoot(), "profiles"));
        var p = new DeviceCalibrationProfile { Name = "Rig", DeviceModel = "Camera" };
        Assert.Throws<InvalidDataException>(() => store.Save(p with { Id = "../outside" }));
        Assert.Throws<InvalidDataException>(() => store.Save(p with { SchemaVersion = 2 }));
        Assert.Throws<InvalidDataException>(() => store.Save(p with { Kind = (CalibrationDeviceKind)99 }));
        Assert.Throws<InvalidDataException>(() => store.Save(p with { InputState = (CaptureSignalState)99 }));
        Assert.Throws<InvalidDataException>(() => store.Save(p with { MeasurementTarget = (CaptureMeasurementTarget)99 }));
        Assert.Throws<InvalidDataException>(() => store.Save(p with { Scanner = new() { Mode = (ScannerAcquisitionMode)99 } }));
        Assert.Throws<InvalidDataException>(() => store.Save(p with { Scanner = new() { BlackCorrection = (UpstreamCorrectionState)99 } }));
        Assert.Throws<InvalidDataException>(() => store.Save(p with { Camera = null! }));
        Assert.Throws<InvalidDataException>(() => store.Save(p with { Scanner = new() { Workflow = (ScannerCalibrationWorkflow)99 } }));
        Assert.Throws<InvalidDataException>(() => store.Save(p with { References = [new(CalibrationReferenceRole.Dark, "relative.raw", new string('0', 64))] }));
    }

    [Fact]
    public void Export_never_overwrites_a_registered_reference()
    {
        string reference = Path.Combine(NewRoot(), "reference.json"); File.WriteAllText(reference, "original");
        var p = new DeviceCalibrationProfile { Name = "Rig", DeviceModel = "Camera",
            References = [DeviceCalibrationStore.Register(CalibrationReferenceRole.Dark, reference)] };
        Assert.Throws<IOException>(() => DeviceCalibrationStore.Export(p, reference));
        Assert.Equal("original", File.ReadAllText(reference));
    }

    [Fact]
    public void A_corrupt_manifest_is_reported_without_hiding_other_profiles()
    {
        string root = NewRoot(); var store = new DeviceCalibrationStore(root);
        store.Save(new() { Name = "Good rig", DeviceModel = "Camera" });
        File.WriteAllText(Path.Combine(root, "bad.json"), "not json");
        Assert.Equal("Good rig", Assert.Single(store.LoadAll()).Name);
        Assert.Contains("bad.json", Assert.Single(store.LoadErrors));
    }

    [Fact]
    public void Older_reference_collections_load_as_uncharacterized_physical_measurement_drafts()
    {
        string path = Path.Combine(NewRoot(), "old.json");
        File.WriteAllText(path, """
            {"SchemaVersion":1,"Id":"00112233445566778899aabbccddeeff", "Name":"Old rig",
             "DeviceModel":"Camera", "References":[]}
            """);
        var p = DeviceCalibrationStore.Read(path);
        Assert.Equal(CaptureSignalState.Unknown, p.InputState);
        Assert.Equal(CaptureMeasurementTarget.DeviceRelativeTransmission, p.MeasurementTarget);
        Assert.Empty(p.MeasurementDefinition);
        Assert.Empty(p.AlreadyAppliedCorrections);
        Assert.Equal(UpstreamCorrectionState.Unknown, p.Scanner.BlackCorrection);
        Assert.Equal(ScannerAcquisitionMode.Unknown, p.Scanner.Mode);
        Assert.Empty(p.Camera.Lens);
    }

    private static string NewRoot()
    {
        string path = Path.Combine(TestDataIsolation.Root, "device-profile-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path); return path;
    }
}
