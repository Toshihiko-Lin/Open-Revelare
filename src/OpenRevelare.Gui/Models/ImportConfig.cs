using System.Collections.Generic;

namespace OpenRevelare.Gui.Models;

/// <summary>Result of the import dialog — port of Python ImportDialog.ImportResult.</summary>
public sealed class ImportConfig
{
    public List<string> Paths { get; } = new();

    /// <summary>
    /// Roll annotation typed at import time. These fields existed before, but the only place to
    /// enter them was the 印样 dialog — i.e. at the very END of the roll, long after the moment
    /// the camera and the film stock are actually in front of you. Collecting them here does not
    /// lock anything in: the same fields stay editable in the sheet dialog.
    /// </summary>
    public RollNotes Notes { get; } = new();
    /// <summary>true = Path A (窄谱 RGB 合成光，需校正图); false = Path B (宽谱白光).</summary>
    public bool PathA { get; set; }
    public string? CalDir { get; set; }     // Path A calibration directory (R/G/B blank-board RAWs)
    public bool LccEnabled { get; set; }
    public string? LccPath { get; set; }
}
