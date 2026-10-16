using System.Text.Json;
using OpenRevelare.Core;
using OpenRevelare.Gui.Models;
using OpenRevelare.Gui.Services;
using Xunit;

namespace OpenRevelare.Tests;

public sealed class CaptureDeviceLibraryTests
{
    [Fact]
    public void Bodies_and_lenses_are_independent_and_duplicates_are_normalized()
    {
        var cameras = new List<CaptureDevice>();
        var lenses = new List<CaptureDevice>();
        CaptureDeviceLibrary.Remember(cameras, new() { Make = " Nikon ", Model = " FM2 " });
        CaptureDeviceLibrary.Remember(cameras, new() { Make = "Nikon", Model = "FM2" });
        CaptureDeviceLibrary.Remember(cameras, new() { Make = "Nikon", Model = "FM2", SerialNumber = "Second body" });
        CaptureDeviceLibrary.Remember(lenses, new() { Make = "Nikkor", Model = "50mm f/1.8" });
        CaptureDeviceLibrary.Remember(lenses, new());
        Assert.Equal(2, cameras.Count);
        Assert.Single(lenses);
        Assert.All(cameras, d => Assert.Equal(d.Model.Trim(), d.Model));
    }

    [Fact]
    public void Device_history_persists_but_film_exposure_and_date_do_not_become_next_export_defaults()
    {
        var model = new Settings.Model
        {
            FilmCameras = [new CaptureDevice { Make = "Nikon", Model = "FM2" }],
            FilmLenses = [new CaptureDevice { Make = "Nikkor", Model = "50mm f/1.8" }],
            Export = new ExportOptions { FilmCapture = new FilmCaptureMetadata
            {
                Iso = "400", DateTimeOriginal = "2026-10-16 12:34:56", ExposureTime = "1/125",
            } },
        };
        string json = JsonSerializer.Serialize(model);
        var restored = JsonSerializer.Deserialize<Settings.Model>(json)!;
        Assert.Equal("FM2", Assert.Single(restored.FilmCameras).Model);
        Assert.Equal("50mm f/1.8", Assert.Single(restored.FilmLenses).Model);
        Assert.Null(restored.Export.FilmCapture);
        Assert.True(restored.Export.KeepSourceExif);
        Assert.DoesNotContain("2026-10-16 12:34:56", json);
    }

    [Theory]
    [InlineData("-1", "", "")]
    [InlineData("400", "1/0", "")]
    [InlineData("400", "1/125", "2026-99-99")]
    public void Invalid_capture_values_are_rejected_before_export(string iso, string shutter, string date)
        => Assert.Throws<FormatException>(() => new FilmCaptureMetadata
        { Iso = iso, ExposureTime = shutter, DateTimeOriginal = date }.Validate());
}
