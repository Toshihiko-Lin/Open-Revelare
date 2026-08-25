using OpenRevelare.Core;
using OpenRevelare.Gui.Models;
using OpenRevelare.Gui.ViewModels;
using Xunit;

namespace OpenRevelare.Tests;

/// <summary>
/// The render/storage boundary for a frame's live params.
///
/// The bug these pin: the crop tool hides the applied crop so the rect can be dragged over what it
/// excludes, and the view model expressed that by building params with a NULL CropRect. Those same
/// params are what gets written to the frame — so while the tool was open, every path that commits
/// a frame stored "no crop" over a real one. It is silent at the time (the preview is uncropped
/// anyway, because the tool is open) and only shows on the next open, by which point the rect is
/// gone from the .ncproj.
///
/// Creating a virtual copy is the sharpest case: it commits the parent and then clones it, so one
/// click erased the crop on both frames at once.
/// </summary>
public class LiveParamsStorageTests
{
    private static readonly (double X, double Y, double W, double H) Crop = (0.10, 0.20, 0.50, 0.40);

    /// <summary>A params object built for RENDER carries a suppressed crop; the one that reaches
    /// the frame must carry the applied rect instead.</summary>
    [Fact]
    public void Suppressed_crop_is_restored_before_storage()
    {
        // What BuildParams hands back while the crop tool is open.
        var rendered = new FrameParams { CropRect = null };

        FrameParams stored = LiveParams.ForStorage(rendered, cropEditing: true, liveCrop: Crop);

        Assert.Equal(Crop, stored.CropRect);
    }

    /// <summary>The whole point of the tool being open is that the PREVIEW is uncropped. Restoring
    /// the rect for storage must not be mistaken for restoring it for the render — the caller keeps
    /// the two apart, and this test states which one this method is.</summary>
    [Fact]
    public void Restoring_for_storage_leaves_the_rest_of_the_params_alone()
    {
        var rendered = new FrameParams
        {
            CropRect = null, QuarterTurns = 1, FlipH = true, ExposureEv = 1.5,
            SplitCell = (0.0, 0.5, 1.0, 0.5),
        };

        FrameParams stored = LiveParams.ForStorage(rendered, cropEditing: true, liveCrop: Crop);

        Assert.Equal(1, stored.QuarterTurns);
        Assert.True(stored.FlipH);
        Assert.Equal(1.5, stored.ExposureEv);
        Assert.Equal((0.0, 0.5, 1.0, 0.5), stored.SplitCell);
    }

    /// <summary>With the tool closed the crop in hand is already the real one, and nothing is
    /// rewritten — including a genuine "no crop", which must survive as null rather than being
    /// refilled from a stale live rect.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Closed_tool_passes_the_params_through_untouched(bool hasCrop)
    {
        var rendered = new FrameParams { CropRect = hasCrop ? Crop : null };

        // liveCrop deliberately disagrees: with the tool closed it must not be consulted at all.
        FrameParams stored = LiveParams.ForStorage(rendered, cropEditing: false,
                                                   liveCrop: (0.9, 0.9, 0.05, 0.05));

        Assert.Equal(hasCrop ? Crop : null, stored.CropRect);
    }

    /// <summary>Clearing the crop while the tool is open is a real edit and must stick: the live
    /// rect is null, so storage stores null. Otherwise 取消裁切 would be undone by the commit.</summary>
    [Fact]
    public void Cleared_crop_stays_cleared()
    {
        var rendered = new FrameParams { CropRect = null };

        FrameParams stored = LiveParams.ForStorage(rendered, cropEditing: true, liveCrop: null);

        Assert.Null(stored.CropRect);
    }

    /// <summary>The virtual-copy path in full: commit the parent while the tool is open, then clone
    /// it. Before the fix the parent was committed with a null crop and the copy inherited it, so
    /// both frames came back uncropped.</summary>
    [Fact]
    public void Virtual_copy_of_a_committed_parent_inherits_the_crop()
    {
        var parent = new RollFrame("/x/IMG_0001.tif");
        parent.Params = LiveParams.ForStorage(new FrameParams { CropRect = null },
                                              cropEditing: true, liveCrop: Crop);

        RollFrame copy = RollFrame.MakeVirtualCopy(parent);

        Assert.Equal(Crop, parent.Params.CropRect);
        Assert.Equal(Crop, copy.Params.CropRect);
        Assert.True(copy.IsVirtual);
    }
}
