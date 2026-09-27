using OpenRevelare.Core;
using Xunit;

namespace OpenRevelare.Tests;

public class ProjectCalibrationDiagnosticsTests
{
    [Fact]
    public void Automatic_endpoint_provenance_round_trips_with_nonfinite_content_dispersion()
    {
        string path = Path.Combine(Path.GetTempPath(), $"revelare-cal-{Guid.NewGuid():N}.ncproj");
        try
        {
            var saved = new Project.Data
            {
                Meta = new Project.RollMeta
                {
                    BaseCalibration = new FilmBaseEstimate(
                        [0.21, 0.18, 0.06], FilmBaseEvidence.ContentInference,
                        0.42, 12, 12, double.NaN, true),
                    HighlightCalibration = new HighlightEndpointEstimate(
                        [1.2, 1.4, 1.6], 0.67, 12, 16, 8.5, 7, 0.031, 95.8,
                        ClippingRisk: true, QuantizationRisk: false),
                },
            };
            saved.Frames.Add(new Project.Frame { SourcePath = "negative.tif" });

            Project.Save(path, saved);
            Project.Data loaded = Project.Load(path);

            Assert.NotNull(loaded.Meta.BaseCalibration);
            Assert.Equal(FilmBaseEvidence.ContentInference, loaded.Meta.BaseCalibration!.Evidence);
            Assert.True(double.IsNaN(loaded.Meta.BaseCalibration.LogDispersion));
            Assert.True(loaded.Meta.BaseCalibration.QuantizationRisk);
            Assert.NotNull(loaded.Meta.HighlightCalibration);
            Assert.Equal(8.5, loaded.Meta.HighlightCalibration!.EffectiveFrames, 10);
            Assert.True(loaded.Meta.HighlightCalibration.ClippingRisk);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
