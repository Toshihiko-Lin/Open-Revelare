using System.Text.Json.Nodes;
using OpenRevelare.Core;
using Xunit;

namespace OpenRevelare.Tests;

public sealed class DustRemovalTests
{
    [Fact]
    public void Repair_is_deterministic_preserves_texture_and_does_not_write_the_source()
    {
        ImageBuffer source = Texture(96, 72);
        float[] clean = (float[])source.Data.Clone();
        int cx = 48, cy = 36;
        for (int y = cy - 2; y <= cy + 2; y++)
        for (int x = cx - 2; x <= cx + 2; x++)
        for (int c = 0; c < 3; c++) source.Data[(y * source.Width + x) * 3 + c] = 0.001f;
        float[] before = (float[])source.Data.Clone();
        var parameters = new FrameParams
        {
            OutputIntent = OutputIntent.None,
            DustEnabled = true,
            DustSpots = new() { new DustSpot(0.505, 0.507, 0.055) },
        };

        var first = new ImageBuffer(source.Width, source.Height, (float[])source.Data.Clone());
        var second = new ImageBuffer(source.Width, source.Height, (float[])source.Data.Clone());
        DustRemoval.Apply(first, parameters.DustSpots);
        DustRemoval.Apply(second, parameters.DustSpots);
        _ = Pipeline.ProcessFrame(source, parameters);

        Assert.Equal(before, source.Data);
        Assert.Equal(first.Data, second.Data);
        int centre = (cy * source.Width + cx) * 3;
        Assert.True(first.Data[centre] > source.Data[centre] + 0.05f);
        for (int c = 0; c < 3; c++)
            Assert.InRange(Math.Abs(first.Data[centre + c] - clean[centre + c]), 0f, 0.06f);
        // Exemplar cloning keeps local variation instead of replacing the repair with one flat tone.
        var repaired = new HashSet<float>();
        for (int y = cy - 2; y <= cy + 2; y++)
        for (int x = cx - 2; x <= cx + 2; x++) repaired.Add(first.Data[(y * source.Width + x) * 3]);
        Assert.True(repaired.Count > 3);
    }

    [Fact]
    public void Overlapping_brush_discs_form_one_repair_area()
    {
        ImageBuffer source = Texture(96, 72);
        DustSpot spot = new(0.505, 0.507, 0.055);
        var once = new ImageBuffer(source.Width, source.Height, (float[])source.Data.Clone());
        var overlapping = new ImageBuffer(source.Width, source.Height, (float[])source.Data.Clone());

        DustRemoval.Apply(once, [spot]);
        DustRemoval.Apply(overlapping, [spot, spot]);

        Assert.Equal(once.Data, overlapping.Data);
    }

    [Fact]
    public void Dust_settings_clone_and_round_trip_through_a_v4_project()
    {
        var parameters = new FrameParams
        {
            DustEnabled = true,
            DustSpots = new()
            {
                new DustSpot(0.2, 0.3, 0.01, false, 1),
                new DustSpot(0.7, 0.6, 0.02, true, 0.8),
            },
        };
        FrameParams clone = parameters.Clone();
        clone.DustSpots.RemoveAt(0);
        Assert.Equal(2, parameters.DustSpots.Count);

        string path = Path.Combine(Path.GetTempPath(), $"openrevelare-dust-{Guid.NewGuid():N}.ncproj");
        try
        {
            var data = new Project.Data();
            data.Frames.Add(new Project.Frame { SourcePath = "frame.tif", Params = parameters });
            Project.Save(path, data);
            JsonNode root = JsonNode.Parse(File.ReadAllText(path))!;
            Assert.Equal(4, root["version"]!.GetValue<int>());
            FrameParams loaded = Project.Load(path).Frames[0].Params;
            Assert.True(loaded.DustEnabled);
            Assert.Equal(parameters.DustSpots, loaded.DustSpots);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Regional_slice_matches_the_same_region_rendered_from_the_full_source()
    {
        ImageBuffer source = Texture(180, 130);
        var parameters = new FrameParams
        {
            OutputIntent = OutputIntent.None,
            DustEnabled = true,
            DustSpots = new()
            {
                new DustSpot(0.46, 0.48, 0.035),
                new DustSpot(0.68, 0.61, 0.025),
            },
        };
        var roi = new RegionRender.Roi(0.34, 0.32, 0.44, 0.46);
        var fromFull = RegionRender.Render(source, parameters, roi);
        var bounds = RegionRender.RequiredSourceBounds(source.Width, source.Height, parameters, roi);
        ImageBuffer slice = Slice(source, bounds);
        var fromSlice = RegionRender.RenderFromSlice(
            slice, bounds.X0, bounds.Y0, source.Width, source.Height, parameters, roi);

        Assert.Equal(fromFull.Realised, fromSlice.Realised);
        Assert.Equal(fromFull.Image.Data, fromSlice.Image.Data);
    }

    private static ImageBuffer Texture(int width, int height)
    {
        var image = new ImageBuffer(width, height);
        for (int y = 0; y < height; y++) for (int x = 0; x < width; x++)
        {
            int p = (y * width + x) * 3;
            float grain = ((x * 37 + y * 19) % 23) / 800f;
            image.Data[p] = 0.30f + 0.25f * x / width + grain;
            image.Data[p + 1] = 0.34f + 0.20f * y / height + grain * 0.8f;
            image.Data[p + 2] = 0.28f + 0.15f * (x + y) / (width + height) + grain * 1.1f;
        }
        return image;
    }

    private static ImageBuffer Slice(ImageBuffer source, (int X0, int Y0, int X1, int Y1) b)
    {
        var result = new ImageBuffer(b.X1 - b.X0, b.Y1 - b.Y0);
        for (int y = b.Y0; y < b.Y1; y++)
            Array.Copy(source.Data, (y * source.Width + b.X0) * 3,
                       result.Data, (y - b.Y0) * result.Width * 3, result.Width * 3);
        return result.InheritSourceFrom(source);
    }
}
