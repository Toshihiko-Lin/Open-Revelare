namespace OpenRevelare.Core;

/// <summary>
/// Linear-light RGB image, float32, interleaved HWC (<c>[r,g,b, r,g,b, ...]</c>).
///
/// Mirrors the Python <c>PipelineImage.data</c> (H, W, 3) float32 array. The flat
/// interleaved layout (base = pixelIndex * 3) keeps the per-pixel address arithmetic to one
/// multiply, which is what lets the hot loops stay simple index walks.
/// </summary>
public sealed class ImageBuffer
{
    public int Width { get; }
    public int Height { get; }

    /// <summary>Length == Width * Height * 3, row-major, channel-interleaved.</summary>
    public float[] Data { get; }

    public ImageBuffer(int width, int height)
    {
        Width = width;
        Height = height;
        Data = new float[checked(width * height * 3)];
    }

    public ImageBuffer(int width, int height, float[] data)
    {
        if (data.Length != checked(width * height * 3))
            throw new ArgumentException($"data length {data.Length} != width*height*3 ({width * height * 3})");
        Width = width;
        Height = height;
        Data = data;
    }

    /// <summary>Number of pixels (not floats).</summary>
    public int PixelCount => Width * Height;

    /// <summary>
    /// One quantisation step of the SOURCE FILE, in this buffer's linear units, or 0 when the
    /// source was continuous (or is simply unknown).
    ///
    /// WHY THIS TRAVELS WITH THE DATA. Density is <c>-log10(T/t_base)</c>, so the uncertainty of a
    /// density read near black is the gap to the adjacent code — and near black that gap is
    /// enormous: on 8-bit, code 1 to 2 is 0.301 D. An endpoint estimator has to know how coarse
    /// its samples are to tell a measurement from pure quantisation, and nothing else in the
    /// buffer records it.
    ///
    /// IT CANNOT BE RECOVERED BY MEASURING THE PIXELS, which is the mistake this property exists
    /// to prevent. <see cref="Resample.Box"/> averages factor² samples, so a 2x preview of an
    /// 8-bit file lands on a quarter-step lattice whose observed minimum gap collapses to float
    /// noise (~1e-9) — a decoder-side "measure the smallest gap" heuristic then reports a step
    /// four orders of magnitude too fine and every sample looks perfectly resolved. The averaging
    /// REDUCES quantisation noise (four samples, half the noise) but does not remove it: a blue
    /// channel crushed to code 0/1 still averages to an anomalously low value, and on the scan
    /// this was written for the preview's blue endpoint came out HIGHER than the full-resolution
    /// one (6.39 against 4.98). So the step is stamped once at decode, where the bit depth is
    /// actually known, and carried forward.
    ///
    /// PROPAGATION RULE: any transform that resamples (box, crop, orientation) keeps the SOURCE
    /// step, because the question it answers — "how coarsely was the underlying film sampled?" —
    /// is a property of the file, not of the current pixel grid. Averaging changes the lattice but
    /// not how much real information the samples carry.
    /// </summary>
    public double SourceQuantisationStep { get; set; }

    /// <summary>
    /// Copy the source lattice from the buffer this one was derived from, and return this buffer.
    ///
    /// Every geometric transform (resample, crop, orientation, straighten) must call it: the step
    /// describes the FILE the pixels came from, so it survives any rearrangement of them. Written
    /// as a fluent one-liner so a transform can stamp its result on the `return` line and the
    /// omission is visible at a glance.
    /// </summary>
    public ImageBuffer InheritSourceFrom(ImageBuffer source)
    {
        SourceQuantisationStep = source.SourceQuantisationStep;
        return this;
    }
}
