using System;
using System.Threading.Tasks;
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

    /// <summary>Write now if dirty, ignoring the idle countdown — roll switch, module switch,
    /// export, shutdown.</summary>
    public async Task FlushAsync()
    {
        if (!_dirty || _saving) return;
        await SaveAsync();
    }

    private async void OnTick(object? sender, EventArgs e)
    {
        if (!_dirty) { _timer.Stop(); return; }
        if (_saving) return;
        if (Environment.TickCount64 - _lastEdit < IdleMs) return;
        await SaveAsync();
    }

    private async Task SaveAsync()
    {
        // Cleared BEFORE the write, not after: an edit that lands while the write is in flight has
        // to leave the roll dirty, or it would be silently dropped until the next unrelated edit.
        _dirty = false;
        _saving = true;
        try { await _flush(); }
        catch { _dirty = true; }        // failed write → keep trying on the next idle pause
        finally
        {
            _saving = false;
            if (!_dirty) _timer.Stop();
        }
    }
}
