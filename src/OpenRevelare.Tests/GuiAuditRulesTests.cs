using OpenRevelare.Gui.Models;
using OpenRevelare.Gui.ViewModels;
using OpenRevelare.Gui.Views;
using Xunit;

namespace OpenRevelare.Tests;

public sealed class GuiAuditRulesTests
{
    [Fact]
    public void Partial_roll_info_edit_preserves_hidden_contact_sheet_metadata()
    {
        var original = new RollNotes { FilmIso = "800 / EI 1600", DevProcess = "C-41 +1", CameraBody = "Old", RollNote = "Old note" };
        var draft = new RollNotes { CameraBody = "New", RollNote = "" };
        var result = RollInfoDialog.MergeVisibleFields(original, draft);
        Assert.Equal("New", result.CameraBody);
        Assert.Empty(result.RollNote); // an explicit clear of a visible field still works
        Assert.Equal("800 / EI 1600", result.FilmIso);
        Assert.Equal("C-41 +1", result.DevProcess);
        Assert.Equal("Old", original.CameraBody); // draft does not mutate the live roll
        Assert.Equal("Old note", original.RollNote);
    }

    [Theory]
    [InlineData(false, false, 0, 2)]
    [InlineData(true, false, 0, 2)]
    [InlineData(true, true, 1, 2)]
    [InlineData(false, true, 1, 2)]
    public void Counted_targets_match_selected_and_whole_roll_rules(bool selectSource, bool selectOther, int selectedCount, int wholeCount)
    {
        var source = new RollFrame("01.tif") { IsSelected = selectSource };
        var other = new RollFrame("02.tif") { IsSelected = selectOther };
        var third = new RollFrame("03.tif");
        var frames = new[] { source, other, third };
        var selected = FrameScope.OtherFrames(frames, source, selectedOnly: true);
        Assert.Equal(selectedCount, selected.Count);
        Assert.Equal(wholeCount, FrameScope.OtherFrames(frames, source, selectedOnly: false).Count);
        Assert.DoesNotContain(source, selected);
        Assert.Equal(selectOther, selected.Contains(other));
        Assert.Empty(FrameScope.OtherFrames(frames, null, false));
    }
}
