using BitMiracle.LibTiff.Classic;
using OpenRevelare.Core;
using OpenRevelare.Gui.Models;
using OpenRevelare.Gui.ViewModels;
using Xunit;

namespace OpenRevelare.Tests;

/// <summary>
/// The HDR controls: a roll-level toggle and a Lightroom-style limit in stops above SDR white.
/// The limit is the MASTER's ceiling, stored in nits; the display under the window feeds only
/// the hint and a new roll's default (D-027) — invariant I5 forbids it from moving the value.
/// </summary>
public sealed class HdrPeakPickerTests
{
    // 宿舍台式 VG27AQ1A：SDR 白 120 nits，面板 520 nits → 余量 4.33× = +2.1 档。
    private static readonly DisplayHdrCapability DormDesktop = new(
        SdrWhiteNits: 120f, Headroom: 520f / 120f, PanelPeakNits: 520f, FailureReason: null);

    [Fact]
    public void Off_by_default_with_a_thousand_nit_limit_waiting()
    {
        using var vm = new MainViewModel();

        Assert.False(vm.HdrEnabled);
        Assert.Equal(2.3, vm.HdrLimitStops, 1e-9);
        Assert.Equal("+2.3 档 · 1000 nits", vm.HdrLimitText);
        Assert.False(vm.HasDisplayHdrStops);
    }

    [Fact]
    public void The_limit_snaps_to_a_tenth_of_a_stop_inside_the_slider_range()
    {
        using var vm = new MainViewModel();

        vm.HdrLimitStops = 1.56;
        Assert.Equal(1.6, vm.HdrLimitStops, 1e-9);

        vm.HdrLimitStops = 9;
        Assert.Equal(MainViewModel.HdrLimitMaxStops, vm.HdrLimitStops, 1e-9);

        vm.HdrLimitStops = 0;
        Assert.Equal(MainViewModel.HdrLimitMinStops, vm.HdrLimitStops, 1e-9);
    }

    [Fact]
    public void The_display_feeds_the_hint_and_never_the_value()
    {
        using var vm = new MainViewModel();
        vm.HdrEnabled = true;
        vm.HdrLimitStops = 4.0;

        vm.SetDisplayHdrCapability(DormDesktop);

        Assert.True(vm.HasDisplayHdrStops);
        Assert.Equal(Math.Log2(520d / 120d), vm.DisplayHdrStops, 1e-6);
        Assert.Equal(4.0, vm.HdrLimitStops, 1e-9);
        Assert.Contains("超出 1.9 档", vm.HdrLimitHint, StringComparison.Ordinal);
        Assert.Contains("高光软校样", vm.HdrLimitHint, StringComparison.Ordinal);

        vm.HdrLimitStops = 2.0;
        Assert.Contains("可完整显示", vm.HdrLimitHint, StringComparison.Ordinal);
    }

    [Fact]
    public void A_panel_whose_peak_cannot_be_read_gets_no_verdict()
    {
        using var vm = new MainViewModel();
        vm.HdrEnabled = true;

        vm.SetDisplayHdrCapability(new DisplayHdrCapability(120f, 1f, null, "IDXGIOutput6 unavailable"));

        Assert.False(vm.HasDisplayHdrStops);
        Assert.Contains("读不到面板峰值", vm.HdrLimitHint, StringComparison.Ordinal);
    }

    // ── D-027: a NEW roll starts at what this display shows in full ─────────────────────────

    [Fact]
    public async Task A_new_roll_starts_on_at_the_displays_headroom_and_keeps_it_when_the_display_changes()
    {
        string path = WriteTiff();
        try
        {
            using var vm = new MainViewModel();
            vm.SetDisplayHdrCapability(DormDesktop);

            await vm.LoadRollAsync(new[] { path });

            // +2.115 stops → +2.1 to the tenth below → 203 × 2^2.1 nits.
            double expectedNits = OutputTarget.ReferenceWhiteNits * Math.Pow(2d, 2.1);
            Assert.All(vm.Frames, f => Assert.Equal(expectedNits, f.Params.HdrPeakNits, 1e-6));
            await WhenSettled(vm);
            Assert.True(vm.HdrEnabled);
            Assert.Equal(2.1, vm.HdrLimitStops, 1e-9);

            // The display is unplugged / leaves HDR mode: the roll's value is its own now (I5).
            vm.SetDisplayHdrCapability(null);
            Assert.True(vm.HdrEnabled);
            Assert.Equal(2.1, vm.HdrLimitStops, 1e-9);
            Assert.All(vm.Frames, f => Assert.Equal(expectedNits, f.Params.HdrPeakNits, 1e-6));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task A_new_roll_on_an_sdr_display_starts_off()
    {
        string path = WriteTiff();
        try
        {
            using var vm = new MainViewModel();

            await vm.LoadRollAsync(new[] { path });
            await WhenSettled(vm);

            Assert.False(vm.HdrEnabled);
            Assert.All(vm.Frames, f => Assert.Equal(0d, f.Params.HdrPeakNits));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Turning_hdr_on_writes_the_limit_to_every_frame_and_off_writes_zero()
    {
        string path = WriteTiff();
        try
        {
            using var vm = new MainViewModel();
            await vm.LoadRollAsync(new[] { path });
            await WhenSettled(vm);

            vm.HdrLimitStops = 1.6;
            vm.HdrEnabled = true;
            double nits = OutputTarget.ReferenceWhiteNits * Math.Pow(2d, 1.6);
            Assert.All(vm.Frames, f => Assert.Equal(nits, f.Params.HdrPeakNits, 1e-6));

            vm.HdrLimitStops = 3.0;
            Assert.All(vm.Frames, f => Assert.Equal(OutputTarget.ReferenceWhiteNits * 8d, f.Params.HdrPeakNits, 1e-6));

            vm.HdrEnabled = false;
            Assert.All(vm.Frames, f => Assert.Equal(0d, f.Params.HdrPeakNits));
            // The limit is remembered for the next time HDR is switched on.
            Assert.Equal(3.0, vm.HdrLimitStops, 1e-9);
        }
        finally { File.Delete(path); }
    }

    private static async Task WhenSettled(MainViewModel vm)
    {
        for (int i = 0; i < 200 && vm.IsBusy; i++) await Task.Delay(25);
        Assert.False(vm.IsBusy, "the first frame never finished decoding");
    }

    private static string WriteTiff()
    {
        string path = Path.Combine(Path.GetTempPath(), $"openrevelare-hdr-picker-{Guid.NewGuid():N}.tif");
        using Tiff tif = Tiff.Open(path, "w") ?? throw new IOException($"could not create test TIFF: {path}");
        tif.SetField(TiffTag.IMAGEWIDTH, 2);
        tif.SetField(TiffTag.IMAGELENGTH, 1);
        tif.SetField(TiffTag.SAMPLESPERPIXEL, 3);
        tif.SetField(TiffTag.BITSPERSAMPLE, 8);
        tif.SetField(TiffTag.ORIENTATION, Orientation.TOPLEFT);
        tif.SetField(TiffTag.PLANARCONFIG, PlanarConfig.CONTIG);
        tif.SetField(TiffTag.PHOTOMETRIC, Photometric.RGB);
        tif.SetField(TiffTag.COMPRESSION, Compression.NONE);
        tif.SetField(TiffTag.ROWSPERSTRIP, 1);
        byte[] row = { 206, 138, 81, 200, 130, 80 };
        if (!tif.WriteScanline(row, 0)) throw new IOException("could not write test TIFF row");
        return path;
    }
}
