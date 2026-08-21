using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using OpenRevelare.Core;

namespace OpenRevelare.Gui.Models;

/// <summary>
/// How one scanned strip is to be cut into frames, while the user is still deciding.
///
/// A strip is described by its DIVIDERS rather than by a list of rects, because that is what
/// the user actually edits and what the detector actually gets wrong. Detection places the
/// frame boundaries accurately — measured frame heights come out even to within a percent —
/// but it can report one frame as two when a blown highlight band inside the picture reads
/// like bare film base. Fixing that means deleting one divider, a single action; the same fix
/// against a list of independent rects would mean editing two of them and keeping their shared
/// edge consistent by hand. Dividers make the shared edge structural: there is only one number,
/// so the frames cannot overlap or leave a gap.
/// </summary>
public sealed partial class StripPlan : ObservableObject
{
    /// <summary>Source scan this plan belongs to.</summary>
    public string Path { get; }

    /// <summary>Which strip of the scan this is, 0-based; 0 when the scan holds only one.</summary>
    public int Index { get; private init; }

    /// <summary>How many strips the scan was found to hold.</summary>
    public int StripCount { get; private init; } = 1;

    /// <summary>Label for the dialog's file list. A multi-strip scan produces several plans
    /// from the one file, so the bare filename would name all of them identically and give the
    /// user no way to tell which row is which; the strip's ordinal is appended in that case
    /// only, leaving the ordinary one-strip scan reading exactly as it did.</summary>
    public string FileName => StripCount > 1
        ? $"{System.IO.Path.GetFileName(Path)} ({Index + 1}/{StripCount})"
        : System.IO.Path.GetFileName(Path);

    /// <summary>Strip's extent across its short axis, normalised — the frames' fixed side edges.</summary>
    public double CrossLo { get; set; }
    public double CrossHi { get; set; }

    /// <summary>True when the strip runs top-to-bottom in the source image.</summary>
    public bool Vertical { get; }

    /// <summary>Where the frames begin and end along the strip, normalised. Two consecutive
    /// entries bound one frame, so N frames have N+1 edges.</summary>
    public List<double> Edges { get; } = new();

    /// <summary>Leave this scan whole — import it as a single frame, uncut.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FrameCount))]
    [NotifyPropertyChangedFor(nameof(Summary))]
    private bool _skipped;

    /// <summary>Low-resolution preview the dialog draws and the detector measured.</summary>
    [ObservableProperty] private Bitmap? _preview;

    /// <summary>Frames this plan currently yields — 1 when skipped or undetected.</summary>
    public int FrameCount => Skipped ? 1 : Math.Max(1, Edges.Count - 1);

    /// <summary>Film-strip caption: the count, or that the scan was left whole.</summary>
    public string Summary => Skipped ? "—" : FrameCount.ToString();

    /// <summary>True when detection found nothing and the edges are a fallback guess.</summary>
    public bool IsFallback { get; private init; }

    private StripPlan(string path, bool vertical)
    {
        Path = path;
        Vertical = vertical;
    }

    /// <summary>
    /// Build the plans for <paramref name="image"/> — one per strip the detector found.
    ///
    /// A flatbed scan often holds several strips side by side, and each is cut independently:
    /// they are separate pieces of film, so their frame boundaries do not line up and one
    /// shared divider list could not describe both. Each strip therefore gets its own plan,
    /// with its own dividers and its own side edges, and the dialog lists them separately.
    ///
    /// When detection returns nothing the scan is still given one plan with evenly spaced
    /// edges rather than no plan at all: a dialog with no dividers gives the user nothing to
    /// grab, and an even split is a far better starting point than making them place every
    /// edge by hand. Such a plan is marked <see cref="IsFallback"/> so the UI can say the
    /// numbers are a guess.
    /// </summary>
    public static IReadOnlyList<StripPlan> Detect(string path, ImageBuffer image)
    {
        var strips = StripSplit.DetectStrips(image);
        bool vertical = image.Height >= image.Width;

        if (strips.Count == 0)
        {
            var fallback = new StripPlan(path, vertical)
            {
                IsFallback = true,
                CrossLo = 0.0,
                CrossHi = 1.0,
            };
            fallback.Edges.Add(0.0);
            fallback.Edges.Add(1.0);
            return new[] { fallback };
        }

        var plans = new List<StripPlan>(strips.Count);
        for (int s = 0; s < strips.Count; s++)
            plans.Add(FromRects(path, vertical, strips[s], s, strips.Count));
        return plans;
    }

    /// <summary>One strip's plan, from that strip's rects.</summary>
    private static StripPlan FromRects(string path, bool vertical, IReadOnlyList<StripSplit.Rect> rects,
                                       int index, int total)
    {
        var plan = new StripPlan(path, vertical)
        {
            Index = index,
            StripCount = total,
        };

        plan.CrossLo = vertical ? rects[0].X : rects[0].Y;
        plan.CrossHi = plan.CrossLo + (vertical ? rects[0].W : rects[0].H);

        // Consecutive rects share an edge, so the boundary list is each rect's start plus the
        // final rect's end. Where the detector left a gap between two frames (the gutter it
        // measured), the edge is placed at the middle of that gap.
        for (int i = 0; i < rects.Count; i++)
        {
            double lo = vertical ? rects[i].Y : rects[i].X;
            double hi = lo + (vertical ? rects[i].H : rects[i].W);
            if (i == 0) plan.Edges.Add(lo);
            else
            {
                double prevHi = plan.Edges[^1];
                plan.Edges[^1] = (prevHi + lo) / 2.0;
            }
            plan.Edges.Add(hi);
        }
        return plan;
    }

    /// <summary>Re-cut into <paramref name="count"/> evenly spaced frames, keeping the strip's
    /// measured extent. This is what the count stepper does — the user overriding the detector
    /// wants a clean regular split, not their previous manual edges rescaled.</summary>
    public void SetFrameCount(int count)
    {
        count = Math.Clamp(count, 1, 24);
        double lo = Edges.Count > 0 ? Edges[0] : 0.0;
        double hi = Edges.Count > 0 ? Edges[^1] : 1.0;
        Edges.Clear();
        for (int i = 0; i <= count; i++) Edges.Add(lo + (hi - lo) * i / count);
        Notify();
    }

    /// <summary>Remove the divider nearest <paramref name="position"/>, merging the two frames
    /// it separated. The outer two edges are the strip's ends and are never removed.</summary>
    public void RemoveDividerNear(double position)
    {
        if (Edges.Count <= 2) return;
        int best = -1;
        double bestDist = double.MaxValue;
        for (int i = 1; i < Edges.Count - 1; i++)
        {
            double d = Math.Abs(Edges[i] - position);
            if (d < bestDist) { bestDist = d; best = i; }
        }
        if (best < 0) return;
        Edges.RemoveAt(best);
        Notify();
    }

    /// <summary>Add a divider at <paramref name="position"/>, splitting the frame it lands in.</summary>
    public void AddDivider(double position)
    {
        if (Edges.Count < 2) return;
        if (position <= Edges[0] || position >= Edges[^1]) return;
        int at = Edges.FindIndex(e => e > position);
        if (at <= 0) return;
        Edges.Insert(at, position);
        Notify();
    }

    private const double MinGap = 0.005;

    /// <summary>Move divider <paramref name="index"/>, kept strictly between its neighbours so
    /// frames can never invert or collapse to nothing.</summary>
    public void MoveDivider(int index, double position)
    {
        if (index <= 0 || index >= Edges.Count - 1) return;
        Edges[index] = Math.Clamp(position, Edges[index - 1] + MinGap, Edges[index + 1] - MinGap);
        Notify();
    }

    /// <summary>
    /// Move the strip's own END — the first or last edge, which bound the outermost frames.
    ///
    /// These are as fallible as any interior divider and were previously fixed for the life of the
    /// plan: detection derives them from where the film reads as film, which lands inside the
    /// picture whenever the frame runs to the very edge of the strip (nothing bright and flat marks
    /// where it stops) or when the scan's blank tail is mistaken for the end of the last frame. The
    /// user could see the box clipping the photograph and had no way to say so.
    /// </summary>
    public void MoveEnd(bool last, double position)
    {
        if (Edges.Count < 2) return;
        if (last) Edges[^1] = Math.Clamp(position, Edges[^2] + MinGap, 1.0);
        else Edges[0] = Math.Clamp(position, 0.0, Edges[1] - MinGap);
        Notify();
    }

    /// <summary>
    /// Move one of the strip's SIDE edges — the frames' shared long edges, across the strip.
    ///
    /// Shared by every frame on the strip by construction: they are all the same width, cut from
    /// the same piece of film. Detection reads them off the film/surround boundary, which is a
    /// different measurement from the frame's own edge — on a scan trimmed close, or one where the
    /// carrier overlaps the image, the two do not coincide and the box eats into the picture.
    /// </summary>
    public void MoveSide(bool high, double position)
    {
        if (high) CrossHi = Math.Clamp(position, CrossLo + MinGap, 1.0);
        else CrossLo = Math.Clamp(position, 0.0, CrossHi - MinGap);
        Notify();
    }

    /// <summary>
    /// The crop rects this plan yields, in source-image coordinates.
    ///
    /// Skipping means "do not cut this strip", which for a lone strip is the whole file. Where
    /// the scan holds several, it is the strip's own box instead: the file also holds the other
    /// strips, so handing back the whole file would import their frames a second time inside
    /// one oversized frame, and the sibling plans would still contribute theirs.
    /// </summary>
    public IReadOnlyList<(double X, double Y, double W, double H)> ToCropRects()
    {
        if (Skipped || Edges.Count < 2)
        {
            if (StripCount <= 1 || Edges.Count < 2)
                return new[] { (0.0, 0.0, 1.0, 1.0) };
            double a = Edges[0], span = Edges[^1] - Edges[0];
            return new[] { Vertical
                ? (CrossLo, a, CrossHi - CrossLo, span)
                : (a, CrossLo, span, CrossHi - CrossLo) };
        }

        var rects = new List<(double, double, double, double)>(Edges.Count - 1);
        for (int i = 0; i < Edges.Count - 1; i++)
        {
            double lo = Edges[i], size = Edges[i + 1] - Edges[i];
            rects.Add(Vertical
                ? (CrossLo, lo, CrossHi - CrossLo, size)
                : (lo, CrossLo, size, CrossHi - CrossLo));
        }
        return rects;
    }

    private void Notify()
    {
        OnPropertyChanged(nameof(Edges));
        OnPropertyChanged(nameof(FrameCount));
        OnPropertyChanged(nameof(Summary));
    }
}
