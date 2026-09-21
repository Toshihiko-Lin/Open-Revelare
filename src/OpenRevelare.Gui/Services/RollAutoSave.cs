using Avalonia.Threading;

namespace OpenRevelare.Gui.Services;

/// <summary>
/// Debounced autosave for the open roll — the reason there is no 「保存工程」 to forget.
///
/// Every parameter change marks the roll dirty; the write happens once the user has been idle for
/// <see cref="IdleMs"/>. Dragging a slider for a minute therefore costs ONE write, not one per
/// frame, and putting the app down mid-edit still lands within a few seconds.
///
/// Deliberately a single 1 s timer rather than a per-edit delay task: edits arrive at slider rate
/// (dozens a second), and minting a CancellationTokenSource for each one is pure churn.
/// </summary>
public sealed class RollAutoSave
{
    private const int IdleMs = 4000;

    private readonly Func<Task> _flush;
    private readonly DispatcherTimer _timer;
    private long _lastEdit;
    private bool _dirty;
    private bool _saving;
    private Task<bool>? _inFlight;

    /// <param name="flush">Persist the roll. Called on the UI thread; may go async internally.</param>
    public RollAutoSave(Func<Task> flush)
    {
        _flush = flush;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += OnTick;
    }

    public bool IsDirty => _dirty;

    /// <summary>Something changed — start (or extend) the idle countdown.</summary>
    public void MarkDirty()
    {
        _lastEdit = Environment.TickCount64;
        _dirty = true;
        if (!_timer.IsEnabled) _timer.Start();
    }

    /// <summary>Forget any pending write WITHOUT saving. For when the roll being tracked is gone
    /// (a new import replaced it) and its edits have already been persisted or abandoned.</summary>
    public void Discard()
    {
        _dirty = false;
        _timer.Stop();
    }

    /// <summary>
    /// Write now if dirty, ignoring the idle countdown — roll switch, module switch, export,
    /// shutdown. Returns false when something is still unsaved afterwards (the write failed).
    ///
    /// A save already in flight is WAITED FOR, then the roll is written again if anything landed
    /// meanwhile. This used to return at once whenever a write was running, and every caller
    /// takes "flushed" to mean "on disk": stepping back to 图库 while the idle-pause save was
    /// still drawing the cover skipped the flush, reopening the roll then read the file back
    /// and discarded the in-memory edits the save had not yet seen — the crops drawn since the
    /// last idle pause were gone.
    /// </summary>
    public async Task<bool> FlushAsync()
    {
        // Loop, not a single await: the write that just finished may have been followed by
        // another (an idle-pause tick can land between two continuations).
        while (_inFlight is { } running) await running;
        // One write, not "until clean": the roll warm-up dirties the cover every time a decode
        // lands, so waiting for a quiet moment would mean waiting for the whole roll.
        return !_dirty || await SaveAsync();
    }

    private async void OnTick(object? sender, EventArgs e)
    {
        if (!_dirty) { _timer.Stop(); return; }
        if (_saving) return;
        if (Environment.TickCount64 - _lastEdit < IdleMs) return;
        await SaveAsync();
    }

    private Task<bool> SaveAsync()
    {
        // One task carries the whole write INCLUDING its bookkeeping, so that by the time an
        // awaiter of _inFlight resumes, _saving and _inFlight already describe the finished
        // state — an awaiter that resumed first would otherwise re-await a completed task forever.
        Task<bool> write = RunFlush();
        // A write that ran to completion synchronously has already cleared _inFlight in its
        // finally; registering it now would leave a completed task for FlushAsync to spin on.
        if (!write.IsCompleted) _inFlight = write;
        return write;
    }

    /// <returns>Whether the write succeeded.</returns>
    private async Task<bool> RunFlush()
    {
        // Cleared BEFORE the write, not after: an edit that lands while the write is in flight has
        // to leave the roll dirty, or it would be silently dropped until the next unrelated edit.
        _dirty = false;
        _saving = true;
        bool ok = true;
        try { await _flush(); }
        catch { _dirty = true; ok = false; }   // failed write → keep trying on the next idle pause
        finally
        {
            _saving = false;
            _inFlight = null;
            if (!_dirty) _timer.Stop();
        }
        return ok;
    }
}
