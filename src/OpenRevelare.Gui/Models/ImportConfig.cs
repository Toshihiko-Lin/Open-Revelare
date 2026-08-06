using System.Collections.Generic;

namespace OpenRevelare.Gui.Models;

/// <summary>Result of the import dialog — port of Python ImportDialog.ImportResult.</summary>
public sealed class ImportConfig
{
    public List<string> Paths { get; } = new();
    /// <summary>true = Path A (窄谱 RGB 合成光，需校正图); false = Path B (宽谱白光).</summary>
    public bool PathA { get; set; }
    public string? CalDir { get; set; }     // Path A calibration directory (R/G/B blank-board RAWs)
    public bool LccEnabled { get; set; }
    public string? LccPath { get; set; }
}
