using System.Reflection;
using OpenRevelare.ColorManagement;
using OpenRevelare.Core;
using OpenRevelare.Gui.Services;
using OpenRevelare.Gui.ViewModels;
using Xunit;

namespace OpenRevelare.Tests;

public sealed class TiffPreviewBatchTests
{
    private const BindingFlags Private = BindingFlags.NonPublic | BindingFlags.Instance;
    private static readonly (double X, double Y, double W, double H)?[] Crops =
        { (0, 0, 1, 0.5), (0.2, 0.3, 0.6, 0.7), (0, 0, 1, 0.5), null };

    [Theory]
    [InlineData(".tif")]
    [InlineData(".fff")]
    [InlineData(".dng")]
    public void Gated_batch_matches_individual_previews_and_source_dimensions(string extension)
    {
        string path = WriteFixture(extension);
        try
        {
            using var engine = new LittleCmsEngine();
            var actual = ImageIo.LoadWorkingPreviewBatch(path, Crops, 31,
                ColorPipelineVersion.ManagedV2, engine, TiffInputAssumption.Linear);
            for (int i = 0; i < Crops.Length; i++)
            {
                var expected = Crops[i] is { } rect
                    ? ImageIo.LoadWorkingPreviewRegion(path, rect, 31, ColorPipelineVersion.ManagedV2, engine, TiffInputAssumption.Linear)
                    : ImageIo.LoadWorkingPreview(path, 31, ColorPipelineVersion.ManagedV2, engine, TiffInputAssumption.Linear);
                Assert.Equal((expected.SourceWidth, expected.SourceHeight), (actual[i].SourceWidth, actual[i].SourceHeight));
                Assert.Equal(expected.Preview.Pixels.Data, actual[i].Preview.Pixels.Data);
                Assert.Equal(expected.Preview.Source, actual[i].Preview.Source);
            }
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData(".fff")]
    [InlineData(".dng")]
    public async Task Foreground_requests_and_duplicate_crops_share_batch_pixels_and_cache_entries(string extension)
    {
        string path = WriteFixture(extension);
        try
        {
            using var vm = new MainViewModel(() => 4L << 30);
            Task<PreviewCache.Entry[]> batch = Batch(vm, path, Crops);
            MethodInfo single = typeof(MainViewModel).GetMethod("PreviewAsync", Private,
                new[] { typeof(string), typeof((double, double, double, double)?), typeof(ColorPipelineVersion), typeof(TiffInputAssumption) })!;
            var foreground = (Task<PreviewCache.Entry>)single.Invoke(vm,
                new object?[] { path, Crops[1], ColorPipelineVersion.ManagedV2, TiffInputAssumption.Linear })!;
            var entries = await batch.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.Same(entries[0], entries[2]);
            Assert.Same(entries[1].Working, (await foreground).Working);
            var cached = await Batch(vm, path, Crops);
            for (int i = 0; i < entries.Length; i++) Assert.Same(entries[i].Working, cached[i].Working);
            Assert.NotEqual(entries[0].Preview.Data, entries[1].Preview.Data);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Batch_failure_completes_all_waiters_and_source_replacement_can_be_decoded()
    {
        string path = WriteFixture(".tif");
        try
        {
            using var vm = new MainViewModel(() => 4L << 30);
            File.WriteAllBytes(path, new byte[16]);
            await Assert.ThrowsAnyAsync<Exception>(() => Batch(vm, path, Crops).WaitAsync(TimeSpan.FromSeconds(30)));
            WriteFixturePixels(path);
            var repaired = await Batch(vm, path, Crops).WaitAsync(TimeSpan.FromSeconds(30));
            Assert.Equal(Crops.Length, repaired.Length);
            Assert.All(repaired, entry => Assert.NotEmpty(entry.Preview.Data));
        }
        finally { File.Delete(path); }
    }

    private static Task<PreviewCache.Entry[]> Batch(MainViewModel vm, string path,
        (double X, double Y, double W, double H)?[] crops)
        => (Task<PreviewCache.Entry[]>)typeof(MainViewModel).GetMethod("PreviewBatchAsync", Private)!
            .Invoke(vm, new object[] { path, crops, ColorPipelineVersion.ManagedV2, TiffInputAssumption.Linear })!;

    private static string WriteFixture(string extension)
    {
        string path = Path.Combine(TestDataIsolation.Root, $"batch-preview-{Guid.NewGuid():N}{extension}");
        if (extension == ".dng") LibRawRuntimeTests.WriteBayerDng(path, width: 1024, height: 768, textured: true);
        else WriteFixturePixels(path);
        return path;
    }

    private static void WriteFixturePixels(string path)
    {
        var image = new ImageBuffer(97, 151);
        for (int i = 0; i < image.Data.Length; i++) image.Data[i] = (i * 97 % 65536) / 65535f;
        TiffIO.ExportTiff16(image, path, TiffIO.CompressionMode.Lzw);
    }
}
