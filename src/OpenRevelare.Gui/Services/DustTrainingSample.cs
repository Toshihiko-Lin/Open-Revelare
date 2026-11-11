using OpenRevelare.Core;
using OpenRevelare.Gui.ViewModels;

namespace OpenRevelare.Gui.Services;

// Owns one private detector workspace. Export leases outlive frame changes or
// explicit cache removal; files disappear when the last reader releases them.
internal sealed class DustTrainingSample : IDisposable
{
    private readonly object _gate = new();
    private int _references = 1;
    private bool _ownerDisposed;
    private IDisposable? _sourceLease;
    public object FrameKey { get; }
    public string Label { get; }
    public string Workspace { get; }
    public MainViewModel.PureNegInput Input { get; }
    public PureNegComponent.ReviewImage Detection { get; }
    public DustMask Automatic { get; }
    public DustMask Final { get; private set; }
    public DustMaskEditor.Stroke[] History { get; private set; } = [];
    public DustReviewProjection.GeometryKey Geometry { get; }
    public IReadOnlySet<int> VisibleIds { get; }

    public DustTrainingSample(object frameKey, string label, string workspace, MainViewModel.PureNegInput input,
        PureNegComponent.ReviewImage detection, DustMask automatic, DustReviewProjection.GeometryKey geometry,
        IEnumerable<int> visibleIds)
    {
        FrameKey = frameKey; Label = label; Workspace = workspace; Input = input;
        Detection = detection; Automatic = Final = automatic; Geometry = geometry;
        VisibleIds = visibleIds.ToHashSet();
    }

    public void UpdateReview(DustMask final, IEnumerable<DustMaskEditor.Stroke> history)
    {
        Final = final;
        History = history.ToArray();
    }

    public DustTrainingSample ForkReview(DustMask final, IEnumerable<DustMaskEditor.Stroke> history)
    {
        var fork = new DustTrainingSample(FrameKey, Label, Workspace, Input, Detection, Automatic, Geometry, VisibleIds)
        { _sourceLease = Acquire() };
        fork.UpdateReview(final, history);
        return fork;
    }

    public IDisposable Acquire()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_references == 0, this);
            _references++;
            return new Lease(this);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_ownerDisposed) return;
            _ownerDisposed = true;
            Release();
        }
    }

    private void Release()
    {
        lock (_gate)
            if (--_references == 0)
            {
                if (_sourceLease is { } source) source.Dispose();
                else PureNegComponent.DeleteTemporaryDirectory(Workspace);
            }
    }

    private sealed class Lease(DustTrainingSample sample) : IDisposable
    {
        private DustTrainingSample? _sample = sample;
        public void Dispose() => Interlocked.Exchange(ref _sample, null)?.Release();
    }
}

internal sealed class DustTrainingBatch : IDisposable
{
    internal sealed record Choice(DustTrainingSample Sample, PureNegTrainingPack.Labels Labels, bool Selected = false);
    private sealed record Entry(DustTrainingSample Sample, PureNegTrainingPack.Labels Labels,
        DustMask Final, DustMaskEditor.Stroke[] History, IDisposable Lease);
    private readonly List<Entry> _entries = [];
    public int Count => _entries.Count;

    public DustTrainingBatch(IEnumerable<Choice> choices)
    {
        try
        {
            foreach (var choice in choices.Where(c => c.Selected).DistinctBy(c => c.Sample))
                _entries.Add(new(choice.Sample, choice.Labels, choice.Sample.Final, choice.Sample.History.ToArray(), choice.Sample.Acquire()));
        }
        catch { Dispose(); throw; }
    }

    public string Export(string parent, CancellationToken ct)
    {
        if (_entries.Count == 0) throw new InvalidOperationException("No training samples were selected.");
        string id = "training-dataset-" + Guid.NewGuid().ToString("N");
        string stage = Path.Combine(parent, "." + id + ".partial");
        string destination = Path.Combine(parent, id);
        Directory.CreateDirectory(stage);
        try
        {
            foreach (var entry in _entries)
            {
                ct.ThrowIfCancellationRequested();
                var sample = entry.Sample;
                PureNegTrainingPack.Export(stage, sample.Detection.SourcePath, sample.Automatic, entry.Final,
                    sample.Detection, sample.VisibleIds, entry.Labels, ct, sample.Geometry, entry.History);
            }
            ct.ThrowIfCancellationRequested();
            Directory.Move(stage, destination);
            return destination;
        }
        finally { PureNegComponent.DeleteTemporaryDirectory(stage); }
    }

    public void Dispose()
    {
        foreach (var entry in _entries) entry.Lease.Dispose();
        _entries.Clear();
    }
}
