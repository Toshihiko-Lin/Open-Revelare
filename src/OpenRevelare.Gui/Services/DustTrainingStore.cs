using System.IO.Compression;
using System.Text.Json;
using OpenRevelare.Core;
using OpenRevelare.Gui.ViewModels;

namespace OpenRevelare.Gui.Services;

// Completed samples are immutable local snapshots, independent of the source photo/project.
// Publishing a new directory only after all files are written keeps interrupted saves invisible.
internal sealed class DustTrainingStore(string root)
{
    private static readonly JsonSerializerOptions Json = new() { IncludeFields = true };
    private sealed record Mask(int Width, int Height, (double X, double Y, double W, double H) Bounds, DustMask.Run[] Runs)
    {
        public static Mask From(DustMask mask) => new(mask.Width, mask.Height, mask.Bounds, mask.EnumerateRuns().ToArray());
        public DustMask Restore() => new(Width, Height, Bounds, Runs);
    }
    private sealed record Snapshot(int Version, string Identity, long SavedTicks, string Label, MainViewModel.PureNegInput Input,
        PureNegComponent.ReviewImage Detection, Mask Automatic, Mask Final,
        DustReviewProjection.GeometryKey Geometry, int[] VisibleIds, DustMaskEditor.Stroke[] History);

    public void Save(DustTrainingSample sample)
    {
        Directory.CreateDirectory(root);
        string id = Guid.NewGuid().ToString("N");
        string stage = Path.Combine(root, "." + id + ".partial");
        Directory.CreateDirectory(stage);
        try
        {
            File.Copy(sample.Detection.SourcePath, Path.Combine(stage, "source.tiff"));
            File.Copy(sample.Detection.RunsPath, Path.Combine(stage, "runs.bin"));
            if (sample.Detection.FibreMapPath is not null)
                File.Copy(sample.Detection.FibreMapPath, Path.Combine(stage, "fibre-map.u8"));
            var review = sample.Detection with { SourcePath = "source.tiff", RunsPath = "runs.bin",
                FibreMapPath = sample.Detection.FibreMapPath is null ? null : "fibre-map.u8" };
            var snapshot = new Snapshot(1, sample.Identity, DateTime.UtcNow.Ticks, sample.Label, sample.Input, review, Mask.From(sample.Automatic),
                Mask.From(sample.Final), sample.Geometry, sample.VisibleIds.ToArray(), sample.History);
            using (var output = File.Create(Path.Combine(stage, "sample.json.gz")))
            using (var gzip = new GZipStream(output, CompressionLevel.Optimal))
                JsonSerializer.Serialize(gzip, snapshot, Json);
            Directory.Move(stage, Path.Combine(root, "sample-" + id));
        }
        finally { PureNegComponent.DeleteTemporaryDirectory(stage); }
    }

    public List<DustTrainingSample> Load(out int unavailable)
    {
        var samples = new Dictionary<string, (long Saved, DustTrainingSample Sample)>();
        unavailable = 0;
        if (!Directory.Exists(root)) return [];
        foreach (string folder in Directory.EnumerateDirectories(root, "sample-*"))
        {
            if (File.Exists(Path.Combine(folder, ".removed"))) continue;
            try
            {
                using var input = File.OpenRead(Path.Combine(folder, "sample.json.gz"));
                using var gzip = new GZipStream(input, CompressionMode.Decompress);
                var snapshot = JsonSerializer.Deserialize<Snapshot>(gzip, Json)
                    ?? throw new InvalidDataException("Missing training sample metadata.");
                if (snapshot.Version != 1 || !Guid.TryParseExact(snapshot.Identity, "N", out _) ||
                    snapshot.Input is null || snapshot.Detection is null || snapshot.Automatic is null ||
                    snapshot.Final is null || snapshot.History is null || snapshot.VisibleIds is null)
                    throw new InvalidDataException("Invalid training sample metadata.");
                // Never trust serialized paths: only the known snapshot files belong to the cache.
                var review = snapshot.Detection with { SourcePath = Path.Combine(folder, "source.tiff"),
                    RunsPath = Path.Combine(folder, "runs.bin"),
                    FibreMapPath = snapshot.Detection.FibreMapPath is null ? null : Path.Combine(folder, "fibre-map.u8") };
                if (!File.Exists(review.SourcePath) || !File.Exists(review.RunsPath) ||
                    (review.FibreMapPath is not null && !File.Exists(review.FibreMapPath)))
                    throw new InvalidDataException("Incomplete training sample.");
                var sample = new DustTrainingSample(new object(), snapshot.Label, folder, snapshot.Input,
                    review, snapshot.Automatic.Restore(), snapshot.Geometry, snapshot.VisibleIds, keepWorkspace: true, identity: snapshot.Identity);
                sample.UpdateReview(snapshot.Final.Restore(), snapshot.History);
                if (!samples.TryGetValue(snapshot.Identity, out var previous) || previous.Saved < snapshot.SavedTicks)
                {
                    previous.Sample?.Dispose();
                    samples[snapshot.Identity] = (snapshot.SavedTicks, sample);
                }
                else sample.Dispose();
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or ArgumentException)
            { unavailable++; }
        }
        return samples.Values.OrderBy(v => v.Saved).Select(v => v.Sample).ToList();
    }

    public void Clear(IEnumerable<string>? leasedWorkspaces = null)
    {
        if (!Directory.Exists(root)) return;
        var leased = (leasedWorkspaces ?? []).Select(Path.GetFullPath).ToHashSet(
            OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (string folder in Directory.EnumerateDirectories(root, "sample-*"))
        {
            File.WriteAllText(Path.Combine(folder, ".removed"), "");
            if (!leased.Contains(Path.GetFullPath(folder))) Directory.Delete(folder, recursive: true);
        }
    }

    // Record equality on lens calibration arrays is reference-based, unlike the persisted value.
    public static bool SameGeometry(DustReviewProjection.GeometryKey a, DustReviewProjection.GeometryKey b) =>
        JsonSerializer.Serialize(a, Json) == JsonSerializer.Serialize(b, Json);
}
