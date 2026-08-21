using OpenRevelare.Core;
using Xunit;

namespace OpenRevelare.Tests;

/// <summary>
/// Frame detection on scans holding more than one strip.
///
/// A flatbed holder takes several strips side by side — a 6×12 sheet is two columns of six — and
/// the detector used to keep only the widest column of film, so half such a scan was silently
/// dropped before the user ever saw the split dialog. These tests pin the two halves of that
/// fix: every strip is found, and nothing that is NOT a strip is admitted as one. The second is
/// the fragile half — the surround between real strips and a dark passage inside a photograph
/// both read as "a gap in the film", and telling them apart is what keeps an ordinary
/// single-frame scan from being reported as several strips.
/// </summary>
public class StripSplitTests
{
    // Levels chosen to match what the real scans measure: surround is essentially unlit, bare
    // film base is the brightest thing on a negative, and the picture sits between them.
    private const float Surround = 0.02f;
    private const float Base = 0.75f;

    /// <summary>Two columns of film, three frames each, separated by black surround — the shape
    /// of a flatbed holder. Both columns must come back, with their own frames.</summary>
    [Fact]
    public void Two_strips_side_by_side_are_both_detected()
    {
        var img = Sheet(strips: 2, framesPerStrip: 3);

        var found = StripSplit.DetectStrips(img);

        Assert.Equal(2, found.Count);
        Assert.All(found, s => Assert.Equal(3, s.Count));
    }

    /// <summary>The strips land where they were drawn: one on each side of the scan, not two
    /// overlapping boxes over the same column.</summary>
    [Fact]
    public void Detected_strips_occupy_separate_columns()
    {
        var found = StripSplit.DetectStrips(Sheet(strips: 2, framesPerStrip: 3));

        Assert.Equal(2, found.Count);
        double leftHi = found[0][0].X + found[0][0].W;
        double rightLo = found[1][0].X;
        Assert.True(leftHi <= rightLo,
            $"strips overlap: left ends at {leftHi:F3}, right starts at {rightLo:F3}");
        Assert.True(found[0][0].X < 0.5, "first strip should be the left column");
        Assert.True(rightLo > 0.5, "second strip should be the right column");
    }

    /// <summary>Three columns work the same way — nothing about the fix is specific to two.</summary>
    [Fact]
    public void Three_strips_are_all_detected()
    {
        var found = StripSplit.DetectStrips(Sheet(strips: 3, framesPerStrip: 2));

        Assert.Equal(3, found.Count);
        Assert.All(found, s => Assert.Equal(2, s.Count));
    }

    /// <summary>A single strip is unaffected: the multi-strip pass must not change what the
    /// ordinary one-column scan — by far the common case — already did.</summary>
    [Fact]
    public void Single_strip_still_yields_one_strip()
    {
        var found = StripSplit.DetectStrips(Sheet(strips: 1, framesPerStrip: 4));

        Assert.Single(found);
        Assert.Equal(4, found[0].Count);
    }

    /// <summary>
    /// A dark band inside the picture is not a second strip.
    ///
    /// This is the regression that the width test alone does not catch. A cropped scan with no
    /// surround at all still produces separate bright column-runs wherever something dark crosses
    /// the frame, and admitting those put a spurious extra column of "frames" into the dialog —
    /// on a real single-frame sample it turned 2 detected frames into 5. Only a gap that goes
    /// near-black relative to the film counts.
    /// </summary>
    [Fact]
    public void Dark_band_inside_the_picture_is_not_a_second_strip()
    {
        var img = Sheet(strips: 1, framesPerStrip: 3);
        // A wide column of shadow across the whole strip: much darker than the picture around it,
        // but nowhere near the surround, exactly like a dark passage in a photograph.
        int lo = (int)(img.Width * 0.45), hi = (int)(img.Width * 0.55);
        for (int y = 0; y < img.Height; y++)
            for (int x = lo; x < hi; x++)
            {
                int b = (y * img.Width + x) * 3;
                if (img.Data[b] > Surround * 2) Set(img, x, y, 0.30f);
            }

        var found = StripSplit.DetectStrips(img);

        Assert.Single(found);
    }

    /// <summary><see cref="StripSplit.Detect"/> still answers for one strip only, so callers that
    /// cannot express more than one keep working rather than seeing a merged mess.</summary>
    [Fact]
    public void Legacy_Detect_returns_the_first_strip_only()
    {
        var img = Sheet(strips: 2, framesPerStrip: 3);

        Assert.Equal(3, StripSplit.Detect(img).Count);
    }

    /// <summary>An empty scan holds no film and must report nothing rather than one huge frame.</summary>
    [Fact]
    public void Blank_scan_detects_nothing()
    {
        var img = new ImageBuffer(200, 600);
        for (int i = 0; i < img.Data.Length; i++) img.Data[i] = Surround;

        Assert.Empty(StripSplit.DetectStrips(img));
    }

    // ── synthetic scans ───────────────────────────────────────────────────────────

    /// <summary>
    /// A scan of <paramref name="strips"/> vertical film strips on a dark bed, each carrying
    /// <paramref name="framesPerStrip"/> pictures separated by bright bare base.
    ///
    /// The picture is textured rather than flat, because the detector's whole rule is
    /// flat-and-bright: a frame painted a uniform colour would read as one long gutter and the
    /// synthetic scan would prove nothing about real film.
    /// </summary>
    private static ImageBuffer Sheet(int strips, int framesPerStrip)
    {
        const int width = 240, height = 900;
        var img = new ImageBuffer(width, height);
        for (int i = 0; i < img.Data.Length; i++) img.Data[i] = Surround;

        // Equal columns of film with a gap of surround between and around them.
        double slot = (double)width / strips;
        int margin = (int)(slot * 0.12);
        int gutter = height / (framesPerStrip * 14);

        var rng = new Random(7);
        for (int s = 0; s < strips; s++)
        {
            int x0 = (int)(s * slot) + margin, x1 = (int)((s + 1) * slot) - margin;
            for (int f = 0; f < framesPerStrip; f++)
            {
                int y0 = f * height / framesPerStrip, y1 = (f + 1) * height / framesPerStrip;
                for (int y = y0; y < y1; y++)
                    for (int x = x0; x < x1; x++)
                    {
                        // Bare base in the gutters at each end of the frame, picture between.
                        // The base carries a little grain rather than being perfectly flat:
                        // FilmExtent separates film from machine output on variance alone, and a
                        // mathematically flat gutter reads as "not film" and is cut away with the
                        // surround — real base measures about σ=0.002, so that is what is drawn.
                        bool inGutter = y < y0 + gutter || y >= y1 - gutter;
                        Set(img, x, y, inGutter
                            ? Base + (float)(rng.NextDouble() - 0.5) * 0.006f
                            : 0.20f + (float)rng.NextDouble() * 0.30f);
                    }
            }
        }
        return img;
    }

    private static void Set(ImageBuffer img, int x, int y, float v)
    {
        int b = (y * img.Width + x) * 3;
        img.Data[b] = img.Data[b + 1] = img.Data[b + 2] = v;
    }
}
