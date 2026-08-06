using CommunityToolkit.Mvvm.ComponentModel;

namespace OpenRevelare.Gui.Models;

/// <summary>
/// Roll-level annotation (metadata only) — port of Python RollMeta's note fields.
/// Never read by the pipeline. These used to be folded into every export's EXIF; they now feed
/// exactly one thing: the info bar burned onto the bottom of the contact sheet
/// (<see cref="Services.SheetInfoBar"/>), edited in the 印样 dialog. Nothing is written to EXIF.
/// </summary>
public sealed partial class RollNotes : ObservableObject
{
    [ObservableProperty] private string _cameraBody = "";
    [ObservableProperty] private string _filmStock = "";
    [ObservableProperty] private string _filmIso = "";
    [ObservableProperty] private string _rollNumber = "";
    [ObservableProperty] private string _devLab = "";
    [ObservableProperty] private string _devProcess = "";
    [ObservableProperty] private string _devDate = "";
    [ObservableProperty] private string _location = "";
    [ObservableProperty] private string _rollNote = "";
    [ObservableProperty] private string _format = "";

    /// <summary>
    /// Blank every field. Called when a NEW roll is imported: these notes are per-roll — they are
    /// written into that roll's .ncproj and shown as its subtitle in the catalog — so carrying the
    /// previous roll's over would label a fresh roll with someone else's roll number.
    /// </summary>
    public void Reset()
    {
        CameraBody = ""; FilmStock = ""; FilmIso = ""; RollNumber = "";
        DevLab = ""; DevProcess = ""; DevDate = ""; Location = ""; RollNote = "";
        Format = "";
    }
}
