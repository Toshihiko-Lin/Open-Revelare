namespace OpenRevelare.Gui.Services;

/// <summary>Shared spacing rhythm for the current contact-sheet paper and lab-record template.</summary>
internal static class SheetDesign
{
    public const double ReferenceWidth = 2048;
    public const double SpacingUnit = 8;

    // Four frame gaps form the paper inset, matching the wordmark/metadata inset in the footer.
    // This is part of the template's composition and is reserved before page-aspect fitting.
    public const double PaperInset = SpacingUnit * 4;
}
