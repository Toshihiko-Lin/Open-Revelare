using OpenRevelare.Core;
using OpenRevelare.Gui.Services;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Metadata.Profiles.Exif;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace OpenRevelare.Tests;

public sealed class LensfunIdentificationTests
{
    private const string Xml = """
        <lensdatabase version="1">
          <camera><maker>Acme</maker><model>Body</model><mount>A</mount></camera>
          <lens><maker>Acme</maker><model>Acme Macro 50mm f/2.8</model><mount>A</mount>
            <calibration><distortion model="poly3" focal="50" k1="0.01" /></calibration></lens>
          <lens><maker>Acme</maker><model>Acme Macro 50mm f/2.8</model><mount>B</mount>
            <calibration><vignetting model="pa" focal="50" aperture="8" distance="1" k1="-0.1" /></calibration></lens>
        </lensdatabase>
        """;

    [Fact]
    public void Camera_mount_disambiguates_identical_lens_names()
    {
        var db = LensfunIdentificationDatabase.FromXml(Xml);
        var result = db.Identify(new("ACME", "Body", "macro 50mm F2.8", 50, 8));
        Assert.Equal(LensfunMatchStatus.Matched, result.Status);
        Assert.True(result.Profile!.HasDistortion);
        Assert.False(result.Profile.HasVignetting);
    }

    [Fact]
    public void Unknown_camera_keeps_same_name_profiles_ambiguous()
    {
        var result = LensfunIdentificationDatabase.FromXml(Xml)
            .Identify(new("", "Unknown body", "Acme Macro 50mm f/2.8", 50, 8));
        Assert.Equal(LensfunMatchStatus.Ambiguous, result.Status);
        Assert.Null(result.Profile);
        Assert.Equal(2, result.Candidates.Count);
    }

    [Theory]
    [InlineData("", LensfunMatchStatus.MissingLens)]
    [InlineData("Macro 50mm f/1.8", LensfunMatchStatus.NotFound)]
    [InlineData("Macro 50mm f/28", LensfunMatchStatus.NotFound)]
    public void Missing_or_different_lens_never_guesses_from_focal_length(string lens, LensfunMatchStatus status)
        => Assert.Equal(status, LensfunIdentificationDatabase.FromXml(Xml)
            .Identify(new("Acme", "Body", lens, 50, 8)).Status);

    [Fact]
    public void Bundled_database_identifies_a_real_lens_offline()
    {
        var db = LensfunIdentificationDatabase.Bundled.Value;
        Assert.True(db.Profiles.Count > 500);
        var result = db.Identify(new("Panasonic", "DMC-G1", "Lumix G 14mm f/2.5 Asph.", 14, 4));
        Assert.Equal(LensfunMatchStatus.Matched, result.Status);
        Assert.True(result.Profile!.HasDistortion);
        Assert.True(result.Profile.HasVignetting);
    }

    [Fact]
    public void Capture_reads_equipment_and_exposure_from_metadata()
    {
        string path = Path.Combine(TestDataIsolation.Root, $"lens-metadata-{Guid.NewGuid():N}.jpg");
        try
        {
            using var image = new Image<Rgb24>(2, 2);
            var exif = image.Metadata.ExifProfile = new ExifProfile();
            exif.SetValue(ExifTag.Make, "Acme");
            exif.SetValue(ExifTag.Model, "Body");
            exif.SetValue(ExifTag.LensModel, "Macro 50mm f/2.8");
            exif.SetValue(ExifTag.FocalLength, new Rational(50));
            exif.SetValue(ExifTag.FNumber, new Rational(8));
            image.SaveAsJpeg(path);
            Assert.Equal(new LensCaptureInfo("Acme", "Body", "Macro 50mm f/2.8", 50, 8), LensCaptureInfo.Read(path));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Appending_and_removing_later_files_reuses_first_file_read()
    {
        var reads = new List<string>();
        var preview = new ImportLensPreview(path =>
        {
            reads.Add(path);
            return Task.FromResult<ImportLensPreviewResult?>(null);
        });
        var files = new List<string> { "first.arw" };
        await preview.Get(files.FirstOrDefault());
        files.Add("second.arw");
        await preview.Get(files.FirstOrDefault());
        files.RemoveAt(1);
        await preview.Get(files.FirstOrDefault());
        Assert.Equal(new[] { "first.arw" }, reads);
        files[0] = "replacement.arw";
        await preview.Get(files.FirstOrDefault());
        files.Clear();
        await preview.Get(files.FirstOrDefault());
        Assert.Equal(new[] { "first.arw", "replacement.arw" }, reads);
    }

    [Fact]
    public async Task Replacing_first_file_invalidates_a_pending_read()
    {
        var first = new TaskCompletionSource<ImportLensPreviewResult?>();
        var second = new TaskCompletionSource<ImportLensPreviewResult?>();
        var preview = new ImportLensPreview(path => path == "a.arw" ? first.Task : second.Task);
        var old = preview.Get("a.arw");
        var current = preview.Get("b.arw");
        first.SetResult(null);
        await old;
        Assert.Same(current, preview.Current);
        Assert.False(current.IsCompleted);
        second.SetResult(null);
        await current;
    }

    [Fact]
    public async Task Scanner_input_skips_raw_metadata_and_database_matching()
        => Assert.Null(await new ImportLensPreview().Get("scan.tiff"));
}
