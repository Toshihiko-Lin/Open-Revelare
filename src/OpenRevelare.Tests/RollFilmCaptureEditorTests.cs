using System.Text.Json;
using OpenRevelare.Core;
using OpenRevelare.Gui.Models;
using OpenRevelare.Gui.Services;
using Xunit;

namespace OpenRevelare.Tests;

public sealed class RollFilmCaptureEditorTests
{
    [Fact]
    public void Devices_sync_but_exposures_and_dates_stay_with_the_frame()
    {
        var editor = new RollFilmCaptureEditor(3);
        var film = new FilmCaptureMetadata
        {
            Camera = new() { Model = "FM2" }, Lens = new() { Model = "50mm" },
            Iso = "400", FNumber = "2.8", ExposureTime = "1/125",
            DateTimeOriginal = "2026-10-18 12:00:00",
        };
        editor.Update(0, film);
        Assert.Equal(film, editor.Get(0));
        Assert.Equal("FM2", editor.Get(2).Camera.Model);
        Assert.Equal("50mm", editor.Get(1).Lens.Model);
        Assert.Equal("", editor.Get(1).Iso);
        Assert.Equal("", editor.Get(1).ExposureTime);
        Assert.Equal("", editor.Get(1).DateTimeOriginal);
        editor.Update(1, editor.Get(1) with { FNumber = "8" });
        Assert.Equal("2.8", editor.Get(0).FNumber);
    }

    [Fact]
    public void Camera_and_lens_overrides_are_independent_and_can_rejoin_sync()
    {
        var editor = new RollFilmCaptureEditor(3);
        editor.Update(0, new() { Camera = new() { Model = "FM2" }, Lens = new() { Model = "50mm" } });
        editor.SetSync(1, camera: true, lens: false);
        editor.Update(1, editor.Get(1) with { Lens = new() { Model = "85mm" } });
        editor.SetSync(2, camera: false, lens: true);
        editor.Update(2, editor.Get(2) with { Camera = new() { Model = "F3" } });
        editor.Update(0, editor.Get(0) with { Camera = new() { Model = "F2" }, Lens = new() { Model = "35mm" } });
        Assert.Equal("F2", editor.Get(1).Camera.Model);
        Assert.Equal("85mm", editor.Get(1).Lens.Model);
        Assert.Equal("F3", editor.Get(2).Camera.Model);
        Assert.Equal("35mm", editor.Get(2).Lens.Model);
        editor.SetSync(1, camera: true, lens: true);
        Assert.Equal("35mm", editor.Get(1).Lens.Model);
    }

    [Fact]
    public void Export_snapshot_is_detached_and_resolves_by_frame_order()
    {
        var editor = new RollFilmCaptureEditor(2);
        editor.Update(0, new() { ExposureTime = "1/125" });
        editor.Update(1, new() { ExposureTime = "1/500" });
        var options = new ExportOptions { FrameFilmCaptures = editor.Snapshot() };
        var copy = options.Clone();
        options.FrameFilmCaptures[0] = new() { ExposureTime = "1/30" };
        editor.Update(1, new() { ExposureTime = "1/60" });
        Assert.Equal("1/125", copy.ForFrame(0).FilmCapture!.ExposureTime);
        Assert.Equal("1/500", copy.ForFrame(1).FilmCapture!.ExposureTime);
        Assert.Null(copy.ForFrame(1).FrameFilmCaptures);
        Assert.DoesNotContain("1/500", JsonSerializer.Serialize(copy));
        var saved = new BatchExportStore.SavedJob { FrameFilmCaptures = copy.FrameFilmCaptures };
        var restored = JsonSerializer.Deserialize<BatchExportStore.SavedJob>(JsonSerializer.Serialize(saved))!;
        Assert.Equal("1/500", restored.FrameFilmCaptures![1].ExposureTime);
        Assert.Equal("1/125", new ExportOptions { FilmCapture = new() { ExposureTime = "1/125" } }.ForFrame(0).FilmCapture!.ExposureTime);
    }
}
