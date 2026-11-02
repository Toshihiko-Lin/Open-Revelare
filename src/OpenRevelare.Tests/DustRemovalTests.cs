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

    [Fact]
    public void A_defect_filling_the_brush_does_not_contaminate_the_colour_match()
    {
        var image = new ImageBuffer(128, 128);
        for (int y = 0; y < 128; y++) for (int x = 0; x < 128; x++) for (int c = 0; c < 3; c++)
            image.Data[(y * 128 + x) * 3 + c] = 0.3f + x * 0.001f + y * 0.0008f + c * 0.03f;
        float[] clean = (float[])image.Data.Clone();
        PaintDefect(image, 64, 64, 11.4, 0.001f);
        DustRemoval.Apply(image, [SpotAt(128, 128, 64, 64, 12)]);
        for (int c = 0; c < 3; c++)
            Assert.InRange(Math.Abs(image.Data[(64 * 128 + 64) * 3 + c] - clean[(64 * 128 + 64) * 3 + c]), 0, 0.003f);
    }

    [Fact]
    public void A_neighbouring_marked_defect_is_never_used_as_the_texture_donor()
    {
        var image = new ImageBuffer(200, 160, Enumerable.Repeat(0.6f, 200 * 160 * 3).ToArray());
        PaintDefect(image, 100, 80, 5, 0.01f);
        PaintDefect(image, 114, 80, 5, 0.01f);
        DustRemoval.Apply(image, [SpotAt(200, 160, 100, 80, 6), SpotAt(200, 160, 114, 80, 6)]);
        Assert.InRange(image.Data[(80 * 200 + 100) * 3], 0.59f, 0.61f);
        Assert.InRange(image.Data[(80 * 200 + 114) * 3], 0.59f, 0.61f);
    }

    [Fact]
    public void A_real_edge_continues_through_the_repair()
    {
        var image = new ImageBuffer(160, 120);
        for (int y = 0; y < 120; y++) for (int x = 0; x < 160; x++) for (int c = 0; c < 3; c++)
            image.Data[(y * 160 + x) * 3 + c] = x < 80 ? 0.2f : 0.8f;
        PaintDefect(image, 80, 60, 6, 0.01f);
        DustRemoval.Apply(image, [SpotAt(160, 120, 80, 60, 8)]);
        Assert.InRange(image.Data[(60 * 160 + 78) * 3], 0.18f, 0.22f);
        Assert.InRange(image.Data[(60 * 160 + 82) * 3], 0.78f, 0.82f);
    }

    [Fact]
    public void Repair_keeps_extended_float_values_and_never_touches_pixels_outside_the_mask()
    {
        var image = new ImageBuffer(100, 80);
        for (int i = 0; i < image.PixelCount; i++)
        { image.Data[i * 3] = -0.2f; image.Data[i * 3 + 1] = 1.3f; image.Data[i * 3 + 2] = 0.4f; }
        PaintDefect(image, 50, 40, 3, 0.01f);
        float[] before = (float[])image.Data.Clone();
        DustSpot spot = SpotAt(100, 80, 50, 40, 7);
        bool[] mask = DustRemoval.MakeMask(100, 80, [spot]);
        DustRemoval.Apply(image, [spot]);
        Assert.InRange(image.Data[(40 * 100 + 50) * 3], -0.201f, -0.199f);
        Assert.InRange(image.Data[(40 * 100 + 50) * 3 + 1], 1.299f, 1.301f);
        for (int i = 0; i < mask.Length; i++) if (!mask[i])
            for (int c = 0; c < 3; c++) Assert.Equal(before[i * 3 + c], image.Data[i * 3 + c]);
    }

    [Fact]
    public void The_smallest_brush_overlay_covers_every_modified_pixel()
    {
        ImageBuffer image = Texture(100, 80);
        DustSpot spot = new(50.7 / 100, 40.7 / 80, 0.00025);
        PaintDefect(image, 50, 40, 1, 0.01f);
        float[] before = (float[])image.Data.Clone();
        bool[] mask = DustRemoval.MakeMask(100, 80, [spot]);
        DustRemoval.Apply(image, [spot]);
        Assert.NotEqual(before, image.Data);
        for (int i = 0; i < mask.Length; i++) if (!mask[i])
            for (int c = 0; c < 3; c++) Assert.Equal(before[i * 3 + c], image.Data[i * 3 + c]);
    }

    [Fact]
    public void Odd_offset_regional_decode_chooses_the_same_donor_as_the_full_image()
    {
        ImageBuffer original = Texture(640, 480);
        DustSpot[] spots = [SpotAt(640, 480, 213.5, 187.5, 6), SpotAt(640, 480, 235, 187, 5)];
        PaintDefect(original, 214, 188, 3, 0.01f);
        var full = new ImageBuffer(640, 480, (float[])original.Data.Clone());
        int halo = DustRemoval.RequiredHalo(spots, 640, 480);
        var bounds = (X0: 205 - halo, Y0: 180 - halo, X1: 221 + halo, Y1: 195 + halo);
        ImageBuffer slice = Slice(original, bounds);
        DustRemoval.Apply(full, spots);
        DustRemoval.Apply(slice, spots, 640, 480, bounds.X0, bounds.Y0);
        for (int y = 180; y < 195; y++) for (int x = 205; x < 221; x++) for (int c = 0; c < 3; c++)
            Assert.Equal(full.Data[(y * 640 + x) * 3 + c],
                slice.Data[((y - bounds.Y0) * slice.Width + x - bounds.X0) * 3 + c]);
    }

    [Fact]
    public void A_continuous_overlapping_stroke_uses_clean_texture_beside_the_stroke()
    {
        var image = new ImageBuffer(160, 120, Enumerable.Repeat(0.6f, 160 * 120 * 3).ToArray());
        var spots = new List<DustSpot>();
        for (int x = 48; x <= 112; x += 8)
        {
            spots.Add(SpotAt(160, 120, x, 60, 6));
            PaintDefect(image, x, 60, 3, 0.01f);
        }
        DustRemoval.Apply(image, spots);
        for (int x = 48; x <= 112; x += 8)
            Assert.InRange(image.Data[(60 * 160 + x) * 3], 0.59f, 0.61f);
    }

    [Fact]
    public void A_fully_marked_neighbourhood_is_left_intact_without_a_clean_donor()
    {
        ImageBuffer image = Texture(48, 48);
        var spots = new List<DustSpot>();
        for (int y = 0; y < 48; y += 3) for (int x = 0; x < 48; x += 3)
            spots.Add(SpotAt(48, 48, x, y, 3.84));
        float[] before = (float[])image.Data.Clone();
        DustRemoval.Apply(image, spots);
        Assert.Equal(before, image.Data);
    }

    private static DustSpot SpotAt(int width, int height, double x, double y, double radius) =>
        new((x + 0.5) / width, (y + 0.5) / height, radius / Math.Min(width, height));

    private static void PaintDefect(ImageBuffer image, double cx, double cy, double radius, float value)
    {
        for (int y = 0; y < image.Height; y++) for (int x = 0; x < image.Width; x++)
            if ((x - cx) * (x - cx) + (y - cy) * (y - cy) < radius * radius)
                for (int c = 0; c < 3; c++) image.Data[(y * image.Width + x) * 3 + c] = value;
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
