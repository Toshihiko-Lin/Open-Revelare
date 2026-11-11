using System.Security.Cryptography;
using System.Text.Json;
using OpenRevelare.Core;
using OpenRevelare.Gui.Services;
using OpenRevelare.Gui.ViewModels;
using Xunit;

namespace OpenRevelare.Tests;

public sealed class DustTrainingBatchTests
{
    [Fact]
    public void Unselected_private_samples_never_enter_the_export_and_leases_keep_selected_sources_alive()
    {
        string root = Directory.CreateTempSubdirectory("training-selection-test-").FullName;
        try
        {
            using var selected = MakeSample(root, "public", .25f);
            using var privateSample = MakeSample(root, "private-photo", .75f);
            string expected = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(selected.Detection.SourcePath))).ToLowerInvariant();
            string destination = Directory.CreateDirectory(Path.Combine(root, "exports")).FullName;
            using (var batch = new DustTrainingBatch([
                new(selected, new(false, true), Selected: true),
                new(privateSample, new(true, false)), // Default is deliberately unselected.
            ]))
            {
                Assert.Equal(1, batch.Count);
                selected.Dispose(); privateSample.Dispose();
                Assert.True(Directory.Exists(selected.Workspace));
                Assert.False(Directory.Exists(privateSample.Workspace));
                string exported = batch.Export(destination, CancellationToken.None);
                string pack = Assert.Single(Directory.GetDirectories(exported));
                using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(pack, "pack.json")));
                Assert.Equal(expected, json.RootElement.GetProperty("sourceSha256").GetString());
                Assert.True(json.RootElement.GetProperty("labels").GetProperty("test").GetBoolean());
                Assert.False(json.RootElement.GetProperty("labels").GetProperty("clean").GetBoolean());
                Assert.DoesNotContain("private-photo", json.RootElement.GetRawText());
                Assert.Single(Directory.GetFiles(exported, "source.tiff", SearchOption.AllDirectories));
            }
            Assert.False(Directory.Exists(selected.Workspace));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Empty_selection_writes_nothing_and_cancelled_batches_leave_no_partial_dataset()
    {
        string root = Directory.CreateTempSubdirectory("training-selection-test-").FullName;
        try
        {
            using var sample = MakeSample(root, "sample", .5f);
            string destination = Directory.CreateDirectory(Path.Combine(root, "exports")).FullName;
            using (var empty = new DustTrainingBatch([new(sample, new())]))
                Assert.Throws<InvalidOperationException>(() => empty.Export(destination, CancellationToken.None));
            Assert.Empty(Directory.GetFileSystemEntries(destination));
            using (var batch = new DustTrainingBatch([new(sample, new(), true)]))
                Assert.Throws<OperationCanceledException>(() => batch.Export(destination, new CancellationToken(true)));
            Assert.Empty(Directory.GetFileSystemEntries(destination));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Review_forks_preserve_the_applied_sample_and_release_shared_files_only_after_all_readers_finish()
    {
        string root = Directory.CreateTempSubdirectory("training-selection-test-").FullName;
        try
        {
            using var original = MakeSample(root, "sample", .5f);
            var changed = new DustMask(16, 12, (0, 0, 1, 1), [new(2, 2, 3)]);
            using var fork = original.ForkReview(changed, []);
            Assert.Equal(0, original.Final.PixelCount);
            Assert.Equal(1, fork.Final.PixelCount);
            original.Dispose();
            Assert.True(Directory.Exists(original.Workspace));
            var lease = fork.Acquire();
            fork.Dispose();
            Assert.True(Directory.Exists(original.Workspace));
            lease.Dispose(); lease.Dispose();
            Assert.False(Directory.Exists(original.Workspace));
        }
        finally { Directory.Delete(root, true); }
    }

    private static DustTrainingSample MakeSample(string root, string name, float level)
    {
        string workspace = Directory.CreateDirectory(Path.Combine(root, name)).FullName;
        string source = Path.Combine(workspace, "source.tiff");
        TiffIO.ExportTiff16(new ImageBuffer(16, 12, Enumerable.Repeat(level, 16 * 12 * 3).ToArray()), source);
        var mask = new DustMask(16, 12, (0, 0, 1, 1), []);
        var input = new MainViewModel.PureNegInput(SourceStamp.Capture(source), 16, 12, (0, 0, 1, 1));
        var review = new PureNegComponent.ReviewImage(source, "unused", 16, 12, [], false);
        return new(new object(), name, workspace, input, review, mask,
            new(16, 12, null, null, null, 0, 0, false, false, 0, null), []);
    }
}
