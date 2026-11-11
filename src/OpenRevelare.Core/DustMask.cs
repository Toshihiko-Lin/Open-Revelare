using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace OpenRevelare.Core;

/// <summary>Confirmed automatic dust selections in the source grid, before optical correction.</summary>
public sealed class DustMask
{
    public readonly record struct Run(int Y, int Start, int End);
    public const int RepairHalo = 24;
    private const int MaxRuns = 1_000_000;
    private const int MaxPixels = 4_000_000;
    private readonly Run[] _runs;

    public int Width { get; }
    public int Height { get; }
    public (double X, double Y, double W, double H) Bounds { get; }
    public int PixelCount { get; }
    // The prepared preview key serializes mask metadata, so equally sized selections
    // must still distinguish different marked pixels.
    public string ContentHash { get; }

    public DustMask(int width, int height, (double X, double Y, double W, double H) bounds, IEnumerable<Run> runs)
    {
        if (width <= 0 || height <= 0 || (long)width * height > 300_000_000 ||
            !double.IsFinite(bounds.X + bounds.Y + bounds.W + bounds.H) ||
            bounds.X < 0 || bounds.Y < 0 || bounds.W <= 0 || bounds.H <= 0 ||
            bounds.X + bounds.W > 1.000000001 || bounds.Y + bounds.H > 1.000000001)
            throw new ArgumentException("Invalid automatic dust mask dimensions.");
        Width = width;
        Height = height;
        Bounds = bounds;
        _runs = runs.Take(MaxRuns + 1).ToArray();
        if (_runs.Length > MaxRuns) throw new ArgumentException("Automatic dust mask has too many runs.");
        int previousY = -1, previousEnd = 0;
        long count = 0;
        foreach (Run run in _runs)
        {
            if (run.Y < 0 || run.Y >= height || run.Start < 0 || run.End > width || run.Start >= run.End ||
                run.Y < previousY || (run.Y == previousY && run.Start < previousEnd))
                throw new ArgumentException("Invalid automatic dust mask run.");
            count += run.End - run.Start;
            if (count > MaxPixels) throw new ArgumentException("Automatic dust mask covers too many pixels.");
            previousY = run.Y;
            previousEnd = run.End;
        }
        PixelCount = (int)count;
        ContentHash = Convert.ToHexString(SHA256.HashData(MemoryMarshal.AsBytes(_runs.AsSpan())));
    }

    private DustMask(DustMask source, (double X, double Y, double W, double H) bounds)
    {
        Width = source.Width;
        Height = source.Height;
        Bounds = bounds;
        _runs = source._runs;
        PixelCount = source.PixelCount;
        ContentHash = source.ContentHash;
    }

    public DustMask RelativeTo((double X, double Y, double W, double H) box) =>
        new(this, ((Bounds.X - box.X) / box.W, (Bounds.Y - box.Y) / box.H, Bounds.W / box.W, Bounds.H / box.H));

    public IEnumerable<Run> EnumerateRuns()
    {
        foreach (var run in _runs) yield return run;
    }

    internal string Encode()
    {
        var bytes = new byte[_runs.Length * 12];
        for (int i = 0; i < _runs.Length; i++)
        {
            Run run = _runs[i];
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(i * 12), run.Y);
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(i * 12 + 4), run.Start);
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(i * 12 + 8), run.End);
        }
        return Convert.ToBase64String(bytes);
    }

    internal static DustMask Decode(int width, int height,
        (double X, double Y, double W, double H) bounds, string data)
    {
        if (data.Length > MaxRuns * 16) throw new FormatException("Automatic dust mask is too large.");
        byte[] bytes = Convert.FromBase64String(data);
        if (bytes.Length % 12 != 0) throw new FormatException("Invalid automatic dust mask data.");
        var runs = new Run[bytes.Length / 12];
        for (int i = 0; i < runs.Length; i++)
            runs[i] = new(BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(i * 12)),
                BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(i * 12 + 4)),
                BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(i * 12 + 8)));
        return new DustMask(width, height, bounds, runs);
    }

    public void AddTo(bool[] pixels, int width, int height)
    {
        foreach (int index in PixelIndices(width, height, width, height, 0, 0)) pixels[index] = true;
    }

    private IEnumerable<int> PixelIndices(int width, int height, int fullWidth, int fullHeight, int ox, int oy)
    {
        double xScale = Bounds.W * fullWidth / Width, yScale = Bounds.H * fullHeight / Height;
        double xOrigin = Bounds.X * fullWidth - ox, yOrigin = Bounds.Y * fullHeight - oy;
        foreach (Run run in _runs)
        {
            int y0 = Math.Max(0, (int)Math.Floor(yOrigin + run.Y * yScale + 1e-7));
            int y1 = Math.Min(height, (int)Math.Ceiling(yOrigin + (run.Y + 1) * yScale - 1e-7));
            if (y0 >= y1) continue;
            int x0 = Math.Max(0, (int)Math.Floor(xOrigin + run.Start * xScale + 1e-7));
            int x1 = Math.Min(width, (int)Math.Ceiling(xOrigin + run.End * xScale - 1e-7));
            for (int y = y0; y < y1; y++)
                for (int x = x0; x < x1; x++)
                    yield return y * width + x;
        }
    }

    private static readonly (int X, int Y)[] Directions = [(1, 0), (0, 1), (1, 1), (1, -1)];

    /// <summary>Interpolate along the closest clean, consistent pair of directions. Only
    /// mask pixels are written and a marked pixel is never sampled as a donor.</summary>
    public void Repair(ImageBuffer image, int fullWidth, int fullHeight, int ox = 0, int oy = 0,
        CancellationToken cancellationToken = default)
    {
        int width = image.Width, height = image.Height;
        var targets = new HashSet<int>();
        foreach (int index in PixelIndices(width, height, fullWidth, fullHeight, ox, oy))
            if (targets.Add(index) && targets.Count > MaxPixels)
                throw new InvalidOperationException("The scaled automatic dust mask covers too many pixels.");
        float[] data = image.Data;
        foreach (int target in targets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int x = target % width, y = target / width;
            int left = -1, right = -1, nearest = -1;
            int nearestDistance = int.MaxValue;
            float weight = 0;
            double best = double.PositiveInfinity;
            foreach (var (dx, dy) in Directions)
            {
                int a = Find(x, y, dx, dy, out int da);
                int b = Find(x, y, -dx, -dy, out int db);
                if (a >= 0 && da < nearestDistance) { nearest = a; nearestDistance = da; }
                if (b >= 0 && db < nearestDistance) { nearest = b; nearestDistance = db; }
                if (a < 0 || b < 0) continue;
                double score = (da + db) * (dx != 0 && dy != 0 ? Math.Sqrt(2) : 1);
                for (int channel = 0; channel < 3; channel++)
                {
                    float u = data[a + channel], v = data[b + channel];
                    score += 2 * Math.Abs(u - v) / (Math.Abs(u) + Math.Abs(v) + .001);
                }
                if (score >= best) continue;
                best = score;
                left = a;
                right = b;
                weight = (float)da / (da + db);
            }
            int destination = target * 3;
            if (left >= 0)
                for (int c = 0; c < 3; c++)
                    data[destination + c] = data[left + c] * (1 - weight) + data[right + c] * weight;
            else if (nearest >= 0)
                Array.Copy(data, nearest, data, destination, 3);
        }

        int Find(int x, int y, int dx, int dy, out int distance)
        {
            for (distance = 1; distance <= RepairHalo; distance++)
            {
                x += dx;
                y += dy;
                if ((uint)x >= (uint)width || (uint)y >= (uint)height) break;
                int index = y * width + x;
                if (!targets.Contains(index)) return index * 3;
            }
            return -1;
        }
    }
}
