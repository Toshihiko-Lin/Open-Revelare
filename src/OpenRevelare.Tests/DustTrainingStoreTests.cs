using System.Security.Cryptography;
using System.Text.Json;
using OpenRevelare.Core;
using OpenRevelare.Gui.Services;
using OpenRevelare.Gui.ViewModels;
using Xunit;

namespace OpenRevelare.Tests;

public sealed class DustTrainingStoreTests
{
    [Fact]
    public void Restart_restores_complete_samples_and_exports_after_original_workspace_is_deleted()
    {
        string root = Directory.CreateTempSubdirectory("training-restart-").FullName;
        try
        {
            var store = new DustTrainingStore(Path.Combine(root, "cache"));
            var sample = MakeSample(root);
            var editor = new DustMaskEditor(sample.Automatic);
            Assert.True(editor.Paint([new(8, 6, 2, 0, 0, 2)], false));
            sample.UpdateReview(editor.Mask, editor.History);
            string expected = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(sample.Detection.SourcePath)));
            var geometry = sample.Geometry;
            store.Save(sample);
            sample.Dispose();
            Assert.False(Directory.Exists(sample.Workspace));

            var restored = Assert.Single(new DustTrainingStore(Path.Combine(root, "cache")).Load(out int unavailable));
            Assert.Equal(0, unavailable);
            Assert.Equal(sample.Identity, restored.Identity);
            Assert.Equal(sample.Input.Stamp, restored.Input.Stamp);
            Assert.Equal(sample.Input.Bounds, restored.Input.Bounds);
            Assert.Equal(sample.Input.ThumbnailPng, restored.Input.ThumbnailPng);
            Assert.True(DustTrainingStore.SameGeometry(geometry, restored.Geometry));
            Assert.Equal(sample.Final.ContentHash, restored.Final.ContentHash);
            Assert.Single(restored.History);
            using (var batch = new DustTrainingBatch([new(restored, new(true, false), true)]))
            {
                restored.Dispose(); // Simulates a window closing while export holds its lease.
                string exported = batch.Export(root, CancellationToken.None);
                string pack = Assert.Single(Directory.GetDirectories(exported));
                Assert.Equal(expected, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(pack, "source.tiff")))));
                Assert.True(File.Exists(Path.Combine(pack, "strokes.json")));
                Assert.True(File.Exists(Path.Combine(pack, "fibre-map.u8.gz")));
                using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(pack, "pack.json")));
                Assert.True(json.RootElement.GetProperty("openRevelare").GetProperty("manualChanges").GetBoolean());
            }
            Assert.Single(store.Load(out _)); // Disposing a restored sample never deletes its cache.
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Revised_samples_restore_only_the_latest_version_and_keep_unrelated_samples()
    {
        string root = Directory.CreateTempSubdirectory("training-versions-").FullName;
        try
        {
            var store = new DustTrainingStore(Path.Combine(root, "cache"));
            using var original = MakeSample(root);
            store.Save(original);
            var edit = new DustMaskEditor(original.Automatic);
            Assert.True(edit.Paint([new(6, 6, 2, 0, 0, 2)], false));
            using var revision = original.ForkReview(edit.Mask, edit.History);
            store.Save(revision);
            using var other = MakeSample(root);
            store.Save(other);
            var loaded = store.Load(out int unavailable);
            Assert.Equal(0, unavailable);
            Assert.Equal(2, loaded.Count);
            Assert.Equal(edit.Mask.ContentHash, loaded.Single(s => s.Identity == original.Identity).Final.ContentHash);
            foreach (var sample in loaded) sample.Dispose();
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Incomplete_and_corrupt_snapshots_do_not_hide_good_samples_and_failed_saves_leave_no_partial_entry()
    {
        string root = Directory.CreateTempSubdirectory("training-corrupt-").FullName;
        try
        {
            string cache = Path.Combine(root, "cache");
            var store = new DustTrainingStore(cache);
            using var sample = MakeSample(root);
            store.Save(sample);
            string bad = Directory.CreateDirectory(Path.Combine(cache, "sample-broken")).FullName;
            File.WriteAllText(Path.Combine(bad, "sample.json.gz"), "invalid gzip");
            Directory.CreateDirectory(Path.Combine(cache, ".unfinished.partial"));
            var loaded = store.Load(out int unavailable);
            Assert.Single(loaded); Assert.Equal(1, unavailable);
            foreach (var entry in loaded) entry.Dispose();
            File.Delete(sample.Detection.RunsPath);
            Assert.Throws<FileNotFoundException>(() => store.Save(sample));
            Assert.Single(Directory.GetDirectories(cache, ".*.partial")); // Only the pre-existing interrupted entry.
            store.Clear();
            Assert.Empty(store.Load(out unavailable)); Assert.Equal(0, unavailable);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Clear_hides_retained_samples_immediately_without_breaking_an_active_review_or_export()
    {
        string root = Directory.CreateTempSubdirectory("training-clear-").FullName;
        try
        {
            var store = new DustTrainingStore(Path.Combine(root, "cache"));
            using var source = MakeSample(root);
            store.Save(source);
            var retained = Assert.Single(store.Load(out _));
            using var review = retained.ForkReview(retained.Final, retained.History);
            retained.Dispose();
            using (var export = new DustTrainingBatch([new(review, new(), true)]))
            {
                store.Clear([review.Workspace]);
                review.DeleteWhenReleased();
                Assert.Empty(store.Load(out _));
                Assert.True(Directory.Exists(review.Workspace));
                Assert.True(Directory.Exists(export.Export(root, CancellationToken.None)));
                review.Dispose();
                Assert.True(Directory.Exists(review.Workspace));
            }
            Assert.False(Directory.Exists(retained.Workspace));
            Assert.True(File.Exists(source.Detection.SourcePath));
        }
        finally { Directory.Delete(root, true); }
    }

    private static DustTrainingSample MakeSample(string root)
    {
        string workspace = Directory.CreateDirectory(Path.Combine(root, Guid.NewGuid().ToString("N"))).FullName;
        string source = Path.Combine(workspace, "source.tiff");
        TiffIO.ExportTiff16(new ImageBuffer(16, 12, Enumerable.Repeat(.25f, 16 * 12 * 3).ToArray()), source);
        string runs = Path.Combine(workspace, "runs.bin");
        using (var writer = new BinaryWriter(File.Create(runs)))
        {
            writer.Write("FDRUNS01"u8); writer.Write(0); writer.Write(16); writer.Write(12); writer.Write(0);
        }
        string map = Path.Combine(workspace, "map.u8"); File.WriteAllBytes(map, new byte[16 * 12]);
        var metadata = JsonSerializer.SerializeToElement(new { settings = new { sensitivity = 1 }, timings = new { }, marks = new { }, inference = new { provider = "test" } });
        var detection = new PureNegComponent.ReviewImage(source, runs, 16, 12, [], false,
            TrainingMetadata: metadata, FibreMapPath: map, FibreMapSha256: Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(map))));
        var mask = new DustMask(16, 12, (.1, .2, .7, .6), []);
        return new(new object(), "sample", workspace, new MainViewModel.PureNegInput(SourceStamp.Capture(source), 16, 12, mask.Bounds, [1, 2, 3]),
            detection, mask, new(16, 12, (.1, .2, .7, .6), new EmbeddedLensCalibration("test", DistortionKnots: [1, 1]),
            null, .01, 1, true, false, 2, (.1, .1, .8, .8)), []);
    }
}
