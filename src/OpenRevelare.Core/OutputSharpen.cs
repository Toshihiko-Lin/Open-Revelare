namespace OpenRevelare.Core;

/// <summary>
/// Output sharpening: the unsharp mask a delivery file gets on its way out.
///
/// WHAT IT IS COMPENSATING FOR, AND WHY IT IS THE LAST STEP. Every resampling step loses acutance —
/// an area average of four source pixels is four pixels' worth of edge spread into one — and so does
/// every viewing medium. Output sharpening restores the apparent edge of the file AT THE SIZE IT
/// WILL BE SEEN, which is why it runs after the resize and not before: sharpening at full resolution
/// and then shrinking throws the sharpening away along with the detail it was applied to, and the
/// halo it leaves behind survives the shrink while the detail does not.
///
/// It is NOT capture sharpening. Nothing here tries to undo the lens, the film's own MTF or the
/// demosaic; those belong to the negative and this program does not touch them. This is the last
/// half-percent of an already-finished picture.
///
/// LUMINANCE ONLY. An unsharp mask run per channel sharpens the channels' edges independently, and
/// at a coloured edge — where the channels' edges do not coincide — that is a colour fringe, which
/// on a film scan lands exactly where the grain's chroma noise already is. So the mask is measured
/// on Rec.709 luma and the SAME correction is added to all three channels: the edge gets steeper and
/// the hue at every pixel is left where the render put it.
///
/// IN THE DISPLAY DOMAIN, on purpose. The buffer this receives is display-encoded, and that is the
/// right place: an unsharp mask is a fixed ± around an edge, so in linear light the bright side of
/// an edge gets a correction many times larger in perceived terms than the dark side, and highlight
/// halos are the result. The encoding curve is what makes the two sides symmetric to the eye.
///
/// GRAIN IS DETAIL TO THIS, and a film scan is made of it. The threshold exists for that reason:
/// below it an edge is left alone, so the smooth part of a sky keeps its grain structure instead of
/// having every speck outlined. It cannot separate grain from detail in the textured parts — nothing
/// can — which is why <see cref="Level.None"/> is a real answer and the default.
/// </summary>
public static class OutputSharpen
{
    /// <summary>
    /// How much. The names are the delivery, not the numbers: the same file is over-sharpened on
    /// screen at the setting that looks right in print, because print's own spread eats some of it.
    /// </summary>
    public enum Level
    {
        /// <summary>Nothing at all — the render as it was. Keeps grain exactly as the film left it.</summary>
        None = 0,
        /// <summary>For the web and for screens, where the pixels are the final medium.</summary>
        Low,
        /// <summary>The general-purpose amount, for a file whose destination is not yet known.</summary>
        Standard,
        /// <summary>For print, and for small output where the resize took the most acutance.</summary>
        High,
    }

    /// <summary>
    /// Sigma and amount per level. Sigma is in output pixels: under a pixel, because an output
    /// sharpen works on the edge the resampler left and that edge is one pixel wide by
    /// construction. A larger radius is a look (local contrast), not sharpening, and belongs to the
    /// grade rather than to the write.
    /// </summary>
    private static (float Sigma, float Amount) Settings(Level level) => level switch
    {
        Level.Low => (0.5f, 0.35f),
        Level.Standard => (0.7f, 0.65f),
        Level.High => (0.9f, 1.00f),
        _ => (0f, 0f),
    };

    /// <summary>
    /// Edges whose luma step is smaller than this are left alone. Two display codes out of 255 —
    /// small enough not to flatten real texture, large enough that a flat sky's grain is not
    /// outlined speck by speck.
    /// </summary>
    private const float Threshold = 2f / 255f;

    /// <summary>
    /// The most an unsharp mask may move one sample, as a fraction of the range.
    ///
    /// AN UNSHARP MASK'S OVERSHOOT GROWS WITH THE EDGE, and this program manufactures the hardest
    /// edge there is: the sprocket mask fills the frame's surround with white, right up against
    /// picture that may be near black. Unlimited, a print-strength sharpen draws a black line all
    /// the way around such a frame — an artefact of our own mask, not of the photograph. Twelve
    /// percent is enough overshoot to read as acutance on a photographic edge (which is soft, and
    /// rarely asks for more) and not enough to draw that line.
    /// </summary>
    private const float OvershootLimit = 0.12f;

    /// <summary>Resize then sharpen a bounded display-encoded delivery, preserving the source.</summary>
    public static ImageBuffer ResizeAndApply(ImageBuffer source, int longEdge, bool allowUpscale, Level level)
    {
        ArgumentNullException.ThrowIfNull(source);
        var (width, height) = Resample.LongEdgeSize(source.Width, source.Height, longEdge, allowUpscale);
        var (sigma, amount) = Settings(level);
        if (sigma <= 0 || amount <= 0 || width < 3 || height < 3)
            return Resample.ToLongEdge(source, longEdge, allowUpscale);
        if (width == source.Width && height == source.Height)
        {
            var copy = new ImageBuffer(width, height, (float[])source.Data.Clone()).InheritSourceFrom(source);
            Apply(copy, level);
            return copy;
        }
        float[] Cpu()
        {
            float[] pixels = Resample.ResizeOnCpu(source, width, height);
            ApplyCpu(pixels, width, height, sigma, amount);
            return pixels;
        }
        if (GpuAcceleration.TryResizeSharpen(source, width, height, Kernel(sigma), amount, Threshold, OvershootLimit,
            Cpu, out var accelerated))
            return new ImageBuffer(width, height, accelerated).InheritSourceFrom(source);
        // A combined working set may not fit even when each individual operation does.
        var resized = Resample.ToLongEdge(source, longEdge, allowUpscale);
        Apply(resized, level);
        return resized;
    }

    /// <summary>
    /// Sharpen <paramref name="displayReferred"/> IN PLACE. The buffer must be display-encoded and
    /// bounded to [0,1] — see the remarks for why this is not run on scene-linear or extended data.
    /// <see cref="Level.None"/> returns without touching it.
    /// </summary>
    public static void Apply(ImageBuffer displayReferred, Level level)
    {
        ArgumentNullException.ThrowIfNull(displayReferred);
        var (sigma, amount) = Settings(level);
        if (sigma <= 0f || amount <= 0f) return;

        int w = displayReferred.Width, h = displayReferred.Height;
        if (w < 3 || h < 3) return;   // nothing to sharpen; a kernel would only touch borders

        float[] data = displayReferred.Data;
        float[] Cpu()
        {
            var copy = (float[])data.Clone();
            ApplyCpu(copy, w, h, sigma, amount);
            return copy;
        }
        if (GpuAcceleration.TrySharpen(displayReferred, Kernel(sigma), amount, Threshold, OvershootLimit, Cpu, out float[] accelerated))
        {
            accelerated.CopyTo(data, 0);
            return;
        }
        ApplyCpu(data, w, h, sigma, amount);
    }

    private static void ApplyCpu(float[] data, int w, int h, float sigma, float amount)
        => ApplyByRows(data, w, h, Kernel(sigma), amount);

    private static void ApplyByRows(float[] data, int width, int height, float[] kernel, float amount)
    {
        int radius = kernel.Length / 2;
        const int scratchBytes = 8 << 20;
        int maxRows = (int)(scratchBytes / ((long)width * sizeof(float) * 2));
        bool small = (long)width * height < 262_144;
        int blockRows = ExportExecution.IsSequential || small ? 1 : Math.Clamp(maxRows - 2 * radius, 1, 256);
        int retainedRows = Math.Min(height, blockRows + 2 * radius);
        var luma = new float[checked(width * retainedRows)];
        var horizontal = new float[luma.Length];

        void PrepareRow(int y)
        {
            int input = y * width;
            int row = y % retainedRows * width;
            for (int x = 0; x < width; x++)
                luma[row + x] = LumaAt(data, input + x);
            for (int x = 0; x < width; x++)
            {
                float sum = 0f;
                for (int k = -radius; k <= radius; k++)
                    sum += luma[row + Math.Clamp(x + k, 0, width - 1)] * kernel[k + radius];
                horizontal[row + x] = sum;
            }
        }

        void SharpenRow(int y)
        {
            Span<int> offsets = stackalloc int[kernel.Length];
            int current = y % retainedRows * width;
            for (int k = -radius; k <= radius; k++)
                offsets[k + radius] = Math.Clamp(y + k, 0, height - 1) % retainedRows * width;
            for (int x = 0; x < width; x++)
            {
                float sum = 0f;
                for (int k = 0; k < kernel.Length; k++)
                    sum += horizontal[offsets[k] + x] * kernel[k];
                ApplyCorrection(data, y * width + x, luma[current + x] - sum, amount);
            }
        }

        int prepared = 0;
        for (int start = 0; start < height;)
        {
            int end = Math.Min(height, start + blockRows);
            int bottom = Math.Min(height, end + radius);
            // Finish the input rows and their halo before touching any output RGB. Preserve
            // the trailing halo in the ring for the next block, with original luma intact.
            if (blockRows == 1)
            {
                for (int y = prepared; y < bottom; y++) PrepareRow(y);
                SharpenRow(start);
            }
            else
            {
                ParallelSweep.For(prepared, bottom, PrepareRow);
                ParallelSweep.For(start, end, SharpenRow);
            }
            prepared = bottom;
            start = end;
        }
    }

    private static void ApplyCorrection(float[] data, int i, float delta, float amount)
    {
        if (delta > -Threshold && delta < Threshold) return;
        // Subtract the threshold so the correction grows continuously across the boundary.
        delta -= delta > 0f ? Threshold : -Threshold;
        float add = Math.Clamp(delta * amount, -OvershootLimit, OvershootLimit);
        int p = i * 3;
        data[p] = Math.Clamp(data[p] + add, 0f, 1f);
        data[p + 1] = Math.Clamp(data[p + 1] + add, 0f, 1f);
        data[p + 2] = Math.Clamp(data[p + 2] + add, 0f, 1f);
    }

    private static float LumaAt(float[] data, int i)
    {
        int p = i * 3;
        return 0.2126f * data[p] + 0.7152f * data[p + 1] + 0.0722f * data[p + 2];
    }

    /// <summary>A normalised 1-D Gaussian reaching three sigma, which is where it is worth 1%.</summary>
    private static float[] Kernel(float sigma)
    {
        int radius = Math.Max(1, (int)MathF.Ceiling(sigma * 3f));
        var kernel = new float[radius * 2 + 1];
        float twoSigmaSquared = 2f * sigma * sigma;
        float total = 0f;
        for (int k = -radius; k <= radius; k++)
        {
            float v = MathF.Exp(-(k * k) / twoSigmaSquared);
            kernel[k + radius] = v;
            total += v;
        }
        for (int i = 0; i < kernel.Length; i++) kernel[i] /= total;
        return kernel;
    }
}
