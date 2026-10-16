using System.Text;
using System.Xml.Linq;
using MetadataExtractor;
using MetadataExtractor.Formats.Exif;
using OpenRevelare.ColorManagement;
using OpenRevelare.Core;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Metadata.Profiles.Exif;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;
using Rational = SixLabors.ImageSharp.Rational;

namespace OpenRevelare.Tests;

public sealed class SourceExifTests
{
    [Theory]
    [InlineData("jpg")]
    [InlineData("tif")]
    [InlineData("dng")]
    [InlineData("heic")]
    public void Capture_metadata_survives_all_containers_without_reusing_source_geometry(string extension)
    {
        WithSource((source, folder) =>
        {
            string path = Path.Combine(folder, "positive." + extension);
            RenderedFrame frame = extension == "dng" ? Frame(hdr: false, 96, 64) : Frame(hdr: false);
            switch (extension)
            {
                case "jpg": JpegIO.ExportJpeg(frame, path, sourceExif: source); break;
                case "tif": TiffIO.ExportTiff(frame, path, sourceExif: source); break;
                case "dng": LinearDng.Write(frame.Pixels, ColorSpaces.DisplayP3, path, sourceExif: source); break;
                default: HeicIO.ExportHeic(frame, path, sourceExif: source); break;
            }
            var directories = ReadExport(path, extension);
            var root = directories.OfType<ExifIfd0Directory>().First();
            var exif = directories.OfType<ExifSubIfdDirectory>().First(d => d.ContainsTag(0xa002));
            Assert.Equal("Copy Camera", root.GetString(0x110));
            Assert.Equal("Copy Lens", exif.GetString(0xa434));
            Assert.Equal("2024:02:03 04:05:06", exif.GetString(0x9003));
            Assert.Equal(100, exif.GetInt32(0x8827));
            Assert.InRange(exif.GetRational(0x829a).ToDouble(), 1d / 30 - 1e-9, 1d / 30 + 1e-9);
            Assert.Equal(1, root.GetInt32(274));
            Assert.Equal(frame.Pixels.Width, exif.GetInt32(0xa002));
            Assert.Equal(frame.Pixels.Height, exif.GetInt32(0xa003));
            Assert.Equal(65535, exif.GetInt32(0xa001));
            Assert.Equal("Photographer", root.GetString(315));
            Assert.NotEmpty(directories.OfType<GpsDirectory>());
            Assert.DoesNotContain(directories.OfType<ExifThumbnailDirectory>(), d => d.ContainsTag(0x201));
            Assert.False(exif.ContainsTag(0x927c));
            if (extension == "tif")
            {
                ImageBuffer pixels = TiffIO.LoadTiff(path, inputIsSrgb: false);
                Assert.Equal(17, pixels.Width);
                Assert.Equal(9, pixels.Height);
            }
            if (extension == "dng")
            {
                string baselinePath = Path.Combine(folder, "baseline.dng");
                LinearDng.Write(frame.Pixels, ColorSpaces.DisplayP3, baselinePath);
                ImageBuffer baseline = RawDecode.DecodeRaw(baselinePath);
                ImageBuffer decoded = RawDecode.DecodeRaw(path);
                Assert.Equal(frame.Pixels.Width, decoded.Width);
                Assert.Equal(frame.Pixels.Height, decoded.Height);
                Assert.Equal(baseline.Data, decoded.Data);
            }
        });
    }

    [Theory]
    [InlineData("tif")]
    [InlineData("heic")]
    public void Hdr_containers_keep_capture_exif_and_film_provenance_without_claiming_srgb(string extension)
    {
        WithSource((source, folder) =>
        {
            string path = Path.Combine(folder, "hdr." + extension);
            var metadata = SourceExif.ForExport(source, true,
                new FilmCaptureMetadata { Camera = new CaptureDevice { Make = "Nikon", Model = "FM2" } });
            RenderedFrame frame = Frame(true);
            if (extension == "tif") TiffIO.ExportTiff(frame, path, sourceExif: metadata);
            else HeicIO.ExportHeic(frame, path, sourceExif: metadata);
            var dirs = ImageMetadataReader.ReadMetadata(path);
            Assert.Equal("FM2", dirs.OfType<ExifIfd0Directory>().First().GetString(272));
            var exif = dirs.OfType<ExifSubIfdDirectory>().First(d => d.ContainsTag(0xa001));
            Assert.Equal(65535, exif.GetInt32(0xa001));
            Assert.False(exif.ContainsTag(0x829a));
            // XMP is uncompressed in both containers. Its provenance must accompany the HDR pixels.
            Assert.Contains("DigitizationSourceExif", Encoding.UTF8.GetString(File.ReadAllBytes(path)));
            if (extension == "tif")
            {
                var pixels = TiffIO.LoadTiff(path, inputIsSrgb: false);
                Assert.True(pixels.Data.Max() > 1f);
            }
        });
    }

    [Fact]
    public void Film_capture_replaces_the_whole_exposure_group_and_archives_digitization_separately()
    {
        WithSource((source, folder) =>
        {
            var film = new FilmCaptureMetadata
            {
                Camera = new CaptureDevice { Make = "Nikon", Model = "FM2", SerialNumber = "FilmBody" },
                Lens = new CaptureDevice { Make = "Nikkor", Model = "50mm f/1.8" },
                Iso = "400", ExposureTime = "1/125", FNumber = "2.8",
            };
            SourceExif output = SourceExif.ForExport(source, preserveSource: true, film)!;
            string path = Path.Combine(folder, "film.jpg");
            JpegIO.ExportJpeg(Frame(false), path, sourceExif: output);
            var dirs = ImageMetadataReader.ReadMetadata(path);
            var root = dirs.OfType<ExifIfd0Directory>().First();
            var exif = dirs.OfType<ExifSubIfdDirectory>().First();
            Assert.Equal("FM2", root.GetString(272));
            Assert.Equal("50mm f/1.8", exif.GetString(0xa434));
            Assert.Equal(400, exif.GetInt32(0x8827));
            Assert.InRange(exif.GetRational(0x829a).ToDouble(), 0.0079999, 0.0080001);
            Assert.False(exif.ContainsTag(0x9003)); // Unknown film time must not become the scanning date.
            Assert.Empty(dirs.OfType<GpsDirectory>()); // Scanning location is not the scene's location.
            using var image = Image.Load<Rgb24>(path);
            XDocument xmp = XDocument.Parse(Encoding.UTF8.GetString(image.Metadata.XmpProfile!.ToByteArray()));
            var original = xmp.Descendants().Single(e => e.Name.LocalName == "DigitizationSourceExif");
            var restored = new ExifProfile(Convert.FromBase64String(original.Value));
            Assert.True(restored.TryGetValue(ExifTag.Model, out var camera));
            Assert.Equal("Copy Camera", camera.Value);
            Assert.True(restored.TryGetValue(ExifTag.DateTimeOriginal, out var date));
            Assert.Equal("2024:02:03 04:05:06", date.Value);
        });
    }

    [Fact]
    public void Hdr_gain_map_and_provenance_share_one_xmp_packet_and_size_limit_counts_both()
    {
        WithSource((source, folder) =>
        {
            var metadata = SourceExif.ForExport(source, true,
                new FilmCaptureMetadata { Camera = new CaptureDevice { Model = "FM2" } });
            string path = Path.Combine(folder, "hdr.jpg");
            var fit = JpegIO.ExportGainMapJpeg(Frame(false), Frame(true), ColorSpaces.DisplayP3,
                OutputTarget.Hdr(1000), path, 90, 100_000, sourceExif: metadata);
            using var image = Image.Load<Rgb24>(path);
            string xmp = Encoding.UTF8.GetString(image.Metadata.XmpProfile!.ToByteArray());
            Assert.Contains("GainMap", xmp);
            Assert.Contains("DigitizationSourceExif", xmp);
            Assert.Equal(new FileInfo(path).Length, fit.Bytes);
            Assert.True(fit.Bytes <= 100_000);
        });
    }

    [Fact]
    public void Disabling_source_retention_does_not_disable_manually_entered_film_exif()
    {
        WithSource((source, _) =>
        {
            Assert.Null(SourceExif.ForExport(source, false, null));
            SourceExif film = SourceExif.ForExport(source, false,
                new FilmCaptureMetadata { Camera = new CaptureDevice { Model = "FM2" } })!;
            Assert.DoesNotContain("DigitizationSourceExif", Encoding.UTF8.GetString(film.MergeXmp(null)!));
            var profile = film.CreateProfile(17, 9, true);
            Assert.False(profile.TryGetValue(ExifTag.Artist, out var artist));
            Assert.True(profile.TryGetValue(ExifTag.Model, out var model));
            Assert.Equal("FM2", model.Value);
        });
    }

    private static IReadOnlyList<MetadataExtractor.Directory> ReadExport(string path, string extension)
    {
        return ImageMetadataReader.ReadMetadata(path);
    }

    private static RenderedFrame Frame(bool hdr, int width = 17, int height = 9)
    {
        var pixels = new ImageBuffer(width, height);
        Array.Fill(pixels.Data, hdr ? 2f : 0.4f);
        var profile = BuiltInColorProfiles.For(hdr ? ColorSpaces.LinearExtendedSrgb : ColorSpaces.DisplayP3, ProfileRole.Output);
        var encoding = new CharacterizedPixelEncoding(profile,
            hdr ? ColorReference.SceneReferred : ColorReference.DisplayReferred,
            hdr ? TransferState.LinearInProfilePrimaries : TransferState.ProfileEncoded,
            hdr ? NumericRange.Extended : NumericRange.Normalized);
        var recipe = new OutputRecipe(ColorPipelineVersion.ManagedV2, profile,
            RenderingIntent.RelativeColorimetric, false, "EXIF fixture", "", false);
        return new RenderedFrame(pixels, encoding, recipe, RenderFingerprint.ComputeManaged(pixels, encoding, recipe));
    }

    private static void WithSource(Action<SourceExif, string> action)
    {
        string folder = Path.Combine(TestDataIsolation.Root, "exif-" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(folder);
        try
        {
            string path = Path.Combine(folder, "copy.jpg");
            using (var image = new Image<Rgb24>(64, 32))
            {
                var exif = new ExifProfile();
                exif.SetValue(ExifTag.Make, "Sony"); exif.SetValue(ExifTag.Model, "Copy Camera");
                exif.SetValue(ExifTag.LensModel, "Copy Lens");
                exif.SetValue(ExifTag.ISOSpeedRatings, new ushort[] { 100 });
                exif.SetValue(ExifTag.ExposureTime, new Rational(1, 30));
                exif.SetValue(ExifTag.FNumber, new Rational(8, 1));
                exif.SetValue(ExifTag.DateTimeOriginal, "2024:02:03 04:05:06");
                exif.SetValue(ExifTag.Orientation, (ushort)6);
                exif.SetValue(ExifTag.Artist, "Photographer");
                exif.SetValue(ExifTag.ColorSpace, (ushort)1);
                exif.SetValue(ExifTag.GPSVersionID, new byte[] { 2, 3, 0, 0 });
                exif.SetValue(ExifTag.GPSLatitudeRef, "N");
                exif.SetValue(ExifTag.GPSLatitude, new[] { new Rational(31, 1), new Rational(14, 1), new Rational(0, 1) });
                exif.SetValue(ExifTag.GPSLongitudeRef, "E");
                exif.SetValue(ExifTag.GPSLongitude, new[] { new Rational(121, 1), new Rational(28, 1), new Rational(0, 1) });
                image.Metadata.ExifProfile = exif;
                image.SaveAsJpeg(path);
            }
            action(SourceExif.Read(path)!, folder);
        }
        finally { System.IO.Directory.Delete(folder, recursive: true); }
    }
}
