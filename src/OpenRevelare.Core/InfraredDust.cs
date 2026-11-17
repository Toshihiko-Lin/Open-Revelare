using BitMiracle.LibTiff.Classic;

namespace OpenRevelare.Core;

/// <summary>Dust detection from the independently acquired infrared plane of a scanner RGBI file.</summary>
public static class InfraredDust
{
    public static DustMask Detect(string path, CancellationToken ct = default)
    {
        // Four TIFF samples alone are not evidence of infrared: the fourth may be alpha.
        if (!ScannerDng.IsScannerDng(path))
            throw new NotSupportedException(CoreText.T("红外除尘需要 Epson VueScan 的 16 位 RGBI DNG 文件。"));
        using var tif = TiffIO.OpenInput(path) ?? throw new IOException("Cannot open scanner image.");
        if (tif.GetField(TiffTag.SAMPLESPERPIXEL)[0].ToInt() != 4)
            throw new NotSupportedException(CoreText.T("此扫描文件没有红外通道。"));
        int width = tif.GetField(TiffTag.IMAGEWIDTH)[0].ToInt();
        int height = tif.GetField(TiffTag.IMAGELENGTH)[0].ToInt();
        if (width <= 0 || height <= 0 || (long)width * height > 100_000_000)
            throw new NotSupportedException(CoreText.T("扫描图像过大，暂无法进行红外除尘。"));
        var plane = new float[checked(width * height)];
        var row = new byte[tif.ScanlineSize()];
        for (int y = 0; y < height; y++)
        {
            ct.ThrowIfCancellationRequested();
            if (!tif.ReadScanline(row, y)) throw new IOException("Cannot read infrared scanline.");
            for (int x = 0; x < width; x++)
                plane[y * width + x] = BitConverter.ToUInt16(row, x * 8 + 6) / 65535f;
        }
        return Detect(plane, width, height, ct);
    }

    /// <summary>Find local IR absorption, rejecting broad shading. Samples are linear transmission.</summary>
    public static DustMask Detect(float[] plane, int width, int height, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(plane);
        if (width <= 0 || height <= 0 || (long)width * height != plane.Length)
            throw new ArgumentException("Invalid infrared plane dimensions.");
        // Separable box background needs one float buffer rather than a full double integral image.
        int radius = Math.Clamp(Math.Min(width, height) / 100, 4, 32);
        var horizontal = new float[plane.Length];
        for (int y = 0; y < height; y++)
        {
            ct.ThrowIfCancellationRequested();
            double sum = 0;
            int start = y * width;
            for (int x = 0; x < Math.Min(width, radius); x++) sum += Sample(start + x);
            for (int x = 0; x < width; x++)
            {
                if (x + radius < width) sum += Sample(start + x + radius);
                if (x - radius - 1 >= 0) sum -= Sample(start + x - radius - 1);
                horizontal[start + x] = (float)(sum / (Math.Min(width - 1, x + radius) - Math.Max(0, x - radius) + 1));
            }
        }
        var sums = new double[width];
        for (int y = 0; y < Math.Min(height, radius); y++)
            for (int x = 0; x < width; x++) sums[x] += horizontal[y * width + x];
        var detected = new bool[plane.Length];
        for (int y = 0; y < height; y++)
        {
            ct.ThrowIfCancellationRequested();
            int count = Math.Min(height - 1, y + radius) - Math.Max(0, y - radius) + 1;
            for (int x = 0; x < width; x++)
            {
                if (y + radius < height) sums[x] += horizontal[(y + radius) * width + x];
                if (y - radius - 1 >= 0) sums[x] -= horizontal[(y - radius - 1) * width + x];
                float value = plane[y * width + x];
                double background = sums[x] / count;
                if (float.IsFinite(value) && value >= 0 && background - value > Math.Max(.025, background * .18))
                    // Include the defect fringe, without enlarging the selection on every pass.
                    for (int yy = Math.Max(0, y - 1); yy <= Math.Min(height - 1, y + 1); yy++)
                        for (int xx = Math.Max(0, x - 1); xx <= Math.Min(width - 1, x + 1); xx++)
                            detected[yy * width + xx] = true;
            }
        }
        var runs = new List<DustMask.Run>();
        int pixels = 0;
        for (int y = 0; y < height; y++)
        {
            ct.ThrowIfCancellationRequested();
            for (int x = 0; x < width;)
            {
                if (!detected[y * width + x]) { x++; continue; }
                int start = x++;
                while (x < width && detected[y * width + x]) x++;
                pixels += x - start;
                if (pixels > 4_000_000 || runs.Count >= 1_000_000)
                    throw new NotSupportedException(CoreText.T("红外异常区域过多，请检查胶片是否适合红外除尘。"));
                runs.Add(new(y, start, x));
            }
        }
        return new DustMask(width, height, (0, 0, 1, 1), runs);

        float Sample(int i) => float.IsFinite(plane[i]) ? Math.Clamp(plane[i], 0, 1) : 0;
    }
}
