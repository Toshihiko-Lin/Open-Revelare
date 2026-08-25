using OpenRevelare.Core;

namespace OpenRevelare.Gui.ViewModels;

/// <summary>
/// The boundary between params built to be RENDERED and params about to be STORED.
///
/// <see cref="MainViewModel.BuildParams"/> serves both, and it suppresses fields that describe
/// the picture on screen rather than the photograph — today just the crop, which is hidden while
/// the crop tool is open so the rect can be dragged over what it excludes. Those suppressions are
/// true of one render and false of the frame, so everything on the way to disk comes through here
/// first.
///
/// Deliberately a pure function in its own file: it carries no Avalonia and no view-model state,
/// which is what makes the rule testable rather than a conditional buried in a commit path.
/// </summary>
public static class LiveParams
{
    /// <summary>
    /// Undo <see cref="MainViewModel.BuildParams"/>' render-only suppressions on
    /// <paramref name="rendered"/>, which is modified in place and returned.
    ///
    /// <paramref name="liveCrop">The crop the user last APPLIED</paramref> — the tool's in-progress
    /// draft lives in the view and only reaches the model on apply, so this is the rect that
    /// belongs on disk even mid-edit.
    /// </summary>
    public static FrameParams ForStorage(FrameParams rendered, bool cropEditing,
                                         (double X, double Y, double W, double H)? liveCrop)
    {
        // A suppressed crop is the render saying "draw this frame whole"; stored, the same null
        // says "this frame has no crop", which is permanent and wrong.
        if (cropEditing) rendered.CropRect = liveCrop;
        return rendered;
    }
}
