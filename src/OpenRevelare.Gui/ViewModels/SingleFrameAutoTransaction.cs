namespace OpenRevelare.Gui.ViewModels;

/// <summary>
/// Runs the two dependent measurements of single-frame auto calibration as one transaction.
/// The caller owns the state snapshot; this helper guarantees that every unsuccessful exit,
/// including an exception, restores it exactly once.
/// </summary>
internal static class SingleFrameAutoTransaction
{
    public static bool Run(Func<bool> measureBase, Func<bool> measureHighlight, Action rollback)
    {
        bool committed = false;
        try
        {
            if (!measureBase() || !measureHighlight()) return false;
            committed = true;
            return true;
        }
        finally
        {
            if (!committed) rollback();
        }
    }
}
