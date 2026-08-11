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

    /// <summary>
    /// Run the roll-wide auto-inversion chain (片基 → 亮部 WB → D-max → 黑白场) after the roll
    /// loads. Defaults to the saved preference, and whatever the user picks in the dialog is
    /// written back to it — the checkbox is both this roll's choice and the new default.
    ///
    /// The decision lives here rather than only in 偏好设置 because it is a per-import one: the
    /// chain has to decode every frame, which is the slowest thing an import does, and whether
    /// that is worth it depends on the roll in front of you.
    /// </summary>
    public bool AutoInvert { get; set; } = true;
}
