namespace OpenRevelare.Core;

/// <summary>
/// Workflow transition declaration between FilmBase and SceneBase.
/// Session-level only — not a persisted parameter (port of Python OutputIntent).
/// </summary>
public enum OutputIntent
{
    /// <summary>Stage 2 bypassed; export linear data as-is (no TRC).</summary>
    None,

    /// <summary>Stage 2 active; sRGB TRC applied on export/preview.</summary>
    Basic,
}
