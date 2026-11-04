using OpenRevelare.Gui.ViewModels;
using Xunit;

namespace OpenRevelare.Tests;

public class RollProgressBannerTests
{
    [Fact]
    public async Task Import_banner_survives_nested_stages_and_clears_after_failure()
    {
        using var vm = new MainViewModel();
        Assert.False(vm.ShowRollProgressBanner);
        await Assert.ThrowsAsync<InvalidOperationException>(() => vm.RunImportStageAsync(async () =>
        {
            Assert.True(vm.ShowRollProgressBanner);
            await vm.RunImportStageAsync(() => Task.CompletedTask);
            vm.IsBusy = false;
            vm.StatusText = "Another frame";
            Assert.True(vm.ShowRollProgressBanner);
            throw new InvalidOperationException("Import failed");
        }));
        Assert.False(vm.ShowRollProgressBanner);
    }

    [Fact]
    public async Task Import_hands_off_to_analysis_without_losing_its_progress()
    {
        using var vm = new MainViewModel();
        vm.RollAnalysisProgress = "Analysis 3/12";
        string analysisTitle = vm.RollProgressTitle;
        await vm.RunImportStageAsync(() =>
        {
            Assert.NotEqual(analysisTitle, vm.RollProgressTitle);
            return Task.CompletedTask;
        });
        vm.BackgroundStatus = "Background thumbnail decode";
        vm.IsBusy = false;
        Assert.True(vm.ShowRollProgressBanner);
        Assert.Equal(analysisTitle, vm.RollProgressTitle);
        Assert.Equal("Analysis 3/12", vm.RollProgressDetail);
        vm.RollAnalysisProgress = "";
        Assert.False(vm.ShowRollProgressBanner);
    }

    [Fact]
    public void First_preview_keeps_import_banner_visible_until_decode_ends()
    {
        using var vm = new MainViewModel();
        vm.LoadingImportedPreview = true;
        Assert.True(vm.ShowRollProgressBanner);
        vm.LoadingImportedPreview = false;
        Assert.False(vm.ShowRollProgressBanner);
    }
    [Fact]
    public async Task Dismissal_keeps_work_running_and_the_next_import_reopens_the_popup()
    {
        using var vm = new MainViewModel();
        vm.RollAnalysisProgress = "Analysis 3/12";
        vm.DismissRollAnalysisNotice();
        Assert.False(vm.ShowRollProgressBanner);
        Assert.Equal("Analysis 3/12", vm.RollAnalysisProgress);
        vm.RollAnalysisProgress = "";
        await vm.RunImportStageAsync(() =>
        {
            Assert.True(vm.ShowRollProgressBanner);
            return Task.CompletedTask;
        });
        Assert.False(vm.ShowRollProgressBanner);
    }
    [Theory]
    [InlineData(800, 420)]
    [InlineData(320, 284)]
    [InlineData(180, 144)]
    public void Floating_card_fits_inside_its_content_pane_with_inset(double paneWidth, double expected)
    {
        Assert.Equal(expected, OpenRevelare.Gui.Views.MainWindow.RollProgressCardWidth(paneWidth));
    }
    [Fact]
    public void Thumbnail_work_and_sprocket_detection_keep_independent_progress_lines()
    {
        using var vm = new MainViewModel();
        vm.RollAnalysisProgress = "Sprocket detection 1/3";
        vm.ThumbnailProgress = "Thumbnails 4/20";
        vm.WarmupProgress = "Thumbnails 5/20";
        Assert.Equal("Sprocket detection 1/3", vm.RollProgressDetail);
        Assert.Equal("Thumbnails 5/20", vm.RollProgressDecodeDetail);
        vm.WarmupProgress = "";
        Assert.Equal("Thumbnails 4/20", vm.RollProgressDecodeDetail);
        vm.RollAnalysisProgress = "Analysis complete";
        Assert.Equal("Thumbnails 4/20", vm.RollProgressDecodeDetail);
        vm.ThumbnailProgress = "";
        Assert.Empty(vm.RollProgressDecodeDetail);
    }
}
