namespace OpenRevelare.Gui.ViewModels;

/// <summary>
/// Coalesces all component changes which make up one canonical preview into a single observable
/// revision. Consumers must react to <see cref="Published"/>, not to the individual scene fields,
/// so they can never capture a half-published base/mask/patch generation.
/// </summary>
internal sealed class PresentationRevisionCoordinator
{
    private int _updateDepth;
    private bool _dirty;
    private bool _updateFaulted;

    public long Revision { get; private set; }

    public event Action<long>? Published;

    public void Invalidate()
    {
        _dirty = true;
        if (_updateDepth == 0) PublishIfDirty();
    }

    public void Update(Action mutation)
    {
        ArgumentNullException.ThrowIfNull(mutation);
        if (_updateDepth == 0) _updateFaulted = false;
        _updateDepth++;
        try
        {
            mutation();
        }
        catch
        {
            // A component setter may already have invalidated this batch. Publishing from the
            // finally block would expose precisely the half-written generation this coordinator
            // exists to hide. The caller can repair the state and invalidate a fresh generation.
            _updateFaulted = true;
            throw;
        }
        finally
        {
            _updateDepth--;
            if (_updateDepth == 0)
            {
                if (_updateFaulted) _dirty = false;
                else PublishIfDirty();
            }
        }
    }

    private void PublishIfDirty()
    {
        if (!_dirty) return;
        _dirty = false;
        Revision = checked(Revision + 1);
        Published?.Invoke(Revision);
    }
}
