using System.Text.Json;
using OpenRevelare.Core;
using OpenRevelare.Gui.Models;
using OpenRevelare.Gui.Services;

namespace OpenRevelare.Gui.ViewModels;

public partial class MainViewModel
{
    // Only compact independent measurements are retained, never image buffers or inferred
    // results. Inferences must not become anchors that corroborate their own assumptions.
    private sealed record CropEvidence(SourceStamp Source, FilmAutoCrop.Detection? Detection);
    private readonly Dictionary<string, CropEvidence> _cropEvidence = new(StringComparer.Ordinal);
    private const int CropEvidenceCapacity = 256;
    private int _cropMeasurements;
    internal int AutoCropMeasurementCount => Volatile.Read(ref _cropMeasurements);

    private static bool CompatibleCropScale(FilmAutoCrop.Detection? frame, FilmAutoCrop.Detection anchor)
    {
        if (frame is null || frame.Width != anchor.Width || frame.Height != anchor.Height || frame.Polarity != anchor.Polarity) return false;
        if ((frame.Image.W * frame.Width >= frame.Image.H * frame.Height)
            != (anchor.Image.W * anchor.Width >= anchor.Image.H * anchor.Height)) return false;
        return !double.IsFinite(frame.FilmWidth) || !double.IsFinite(anchor.FilmWidth)
            || Math.Abs(frame.FilmWidth / anchor.FilmWidth - 1) <= .08;
    }

    private string CropEvidenceKey(SourceStamp source, FrameParams p)
    {
        var native = p.EmbeddedLens is { IsUsable: true } n ? n : null;
        var lens = native is null && LensfunCorrections.IsActive(p) && p.Lensfun!.Distortion is not null ? p.Lensfun : null;
        return source.Key + "|" + JsonSerializer.Serialize(new
        {
            Pipeline = _colorPipelineVersion, Input = _tiffInputAssumption,
            p.QuarterTurns, p.FlipH, p.FlipV, p.Rotation, p.DistortionK1,
            Native = native is null ? null : new { native.Warp, native.DistortionKnots, native.CenterX, native.CenterY },
            Lensfun = lens is null ? null : new { lens.CenterX, lens.CenterY, lens.CameraCropFactor, lens.Distortion },
        });
    }

    private void RememberCropEvidence(string key, CropEvidence evidence)
    {
        if (!_cropEvidence.ContainsKey(key) && _cropEvidence.Count >= CropEvidenceCapacity)
            _cropEvidence.Remove(_cropEvidence.Keys.First());
        _cropEvidence[key] = evidence;
    }

    private async Task<(FilmAutoCrop.Detection? Detection, FilmAutoCrop.Suggestion? Suggestion)> MeasureCropAsync(
        RollFrame frame, FrameParams p, FilmCropMode mode, bool allowSuggestions,
        List<SourceStamp> stamps, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var source = SourceStamp.Capture(frame.Path);
        string key = CropEvidenceKey(source, p);
        if (_cropEvidence.TryGetValue(key, out var cached) && (!allowSuggestions || cached.Detection is not null))
        {
            stamps.Add(source);
            RenderTrace.Count("crop.evidence.hit");
            return (cached.Detection, null);
        }
        var entry = await PreviewAsync(frame.Path, null).WaitAsync(token);
        source.RequireCurrent();
        var result = await Task.Run(() =>
        {
            Interlocked.Increment(ref _cropMeasurements);
            using var trace = RenderTrace.Scope("crop.detect");
            var pixels = FilmAutoCrop.Prepare(entry.Preview, p);
            var d = FilmAutoCrop.Detect(pixels);
            bool swap = ((p.QuarterTurns % 2) + 2) % 2 == 1;
            var confirmed = d is null || Math.Abs(p.Rotation + d.RotationCorrection) > StraightenLimit ? null : d with
            {
                Width = swap ? entry.SourceHeight : entry.SourceWidth,
                Height = swap ? entry.SourceWidth : entry.SourceHeight,
                AppliedRotation = p.Rotation,
            };
            var suggestion = allowSuggestions && confirmed is null ? FilmAutoCrop.Suggest(pixels, mode) : null;
            if (suggestion is { } sg && Math.Abs(p.Rotation + sg.RotationCorrection) > StraightenLimit) suggestion = null;
            return (Detection: confirmed, Suggestion: suggestion);
        }, token);
        token.ThrowIfCancellationRequested();
        source.RequireCurrent();
        stamps.Add(source);
        RememberCropEvidence(key, new(source, result.Detection));
        return result;
    }

    private IReadOnlyList<FilmAutoCrop.Detection?> CachedRollCropEvidence(
        (RollFrame Frame, FrameParams Params)[] rollFrames, List<SourceStamp> stamps,
        HashSet<RollFrame>? exclude = null)
    {
        var evidence = new List<FilmAutoCrop.Detection?>();
        var sources = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (frame, p) in rollFrames)
        {
            if (frame.IsVirtual || _splitPaths.Contains(frame.Path) || exclude?.Contains(frame) == true) continue;
            if (SourceStamp.TryCapture(frame.Path) is not { } source || !sources.Add(source.Path)) continue;
            if (!_cropEvidence.TryGetValue(CropEvidenceKey(source, p), out var hit) || hit.Detection is null) continue;
            stamps.Add(source); evidence.Add(hit.Detection);
        }
        return evidence;
    }

    private void RememberAppliedCropEvidence(RollFrame frame, FrameParams before, FrameParams after, FilmAutoCrop.Detection applied)
    {
        if (SourceStamp.TryCapture(frame.Path) is not { } source
            || !_cropEvidence.TryGetValue(CropEvidenceKey(source, before), out var previous)) return;
        // Auto-straightening changes the key. The same rectangle already belongs to the
        // corrected coordinate system, so a subsequent output-mode change can reuse it.
        var detection = !applied.UsedRollReference && previous.Detection is { } measured
            ? measured with { AppliedRotation = after.Rotation, RotationCorrection = 0 } : null;
        RememberCropEvidence(CropEvidenceKey(source, after), new(source, detection));
    }
}
