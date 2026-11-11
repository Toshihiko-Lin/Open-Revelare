using Avalonia;
using OpenRevelare.Core;

namespace OpenRevelare.Gui.Services;

// Candidate IDs live on the uncropped detector grid. Sample them through the same geometry
// as the preview, without blending IDs when the image transform interpolates pixels.
internal sealed class DustReviewProjection
{
    internal readonly record struct GeometryKey(int Width, int Height,
        (double X, double Y, double W, double H)? Margin, EmbeddedLensCalibration? EmbeddedLens,
        LensfunCalibration? Lensfun, double Distortion, int Turns, bool FlipH, bool FlipV,
        double Rotation, (double X, double Y, double W, double H)? Crop);

    private readonly int[] _ids;
    private readonly ImageBuffer _coordinates;
    private readonly int _rawWidth, _rawHeight, _sourceWidth, _sourceHeight;
    private readonly (double X, double Y, double W, double H) _margin, _bounds;
    public int Width { get; }
    public int Height { get; }
    public HashSet<int> VisibleIds { get; } = [];

    public DustReviewProjection(GeometryKey key, int sourceWidth, int sourceHeight,
        (double X, double Y, double W, double H) bounds, PureNegComponent.CandidateRun[] runs)
    {
        int width = key.Width, height = key.Height;
        var margin = key.Margin ?? (X: 0d, Y: 0d, W: 1d, H: 1d);
        _rawWidth = width;
        _rawHeight = height;
        _sourceWidth = sourceWidth;
        _sourceHeight = sourceHeight;
        _margin = margin;
        _bounds = bounds;
        double originX = (bounds.X - margin.X) * width / margin.W;
        double originY = (bounds.Y - margin.Y) * height / margin.H;
        double scaleX = bounds.W * width / (margin.W * sourceWidth);
        double scaleY = bounds.H * height / (margin.H * sourceHeight);
        int[] rawIds = new int[checked(width * height)];
        Array.Fill(rawIds, -1);
        foreach (var (id, run) in runs)
        {
            int x0 = Math.Clamp((int)Math.Floor(originX + run.Start * scaleX + 1e-7), 0, width);
            int x1 = Math.Clamp((int)Math.Ceiling(originX + run.End * scaleX - 1e-7), 0, width);
            int y0 = Math.Clamp((int)Math.Floor(originY + run.Y * scaleY + 1e-7), 0, height);
            int y1 = Math.Clamp((int)Math.Ceiling(originY + (run.Y + 1) * scaleY - 1e-7), 0, height);
            if (x0 >= x1) continue;
            for (int y = y0; y < y1; y++) rawIds.AsSpan(y * width + x0, x1 - x0).Fill(id);
        }

        var coordinates = new ImageBuffer(width, height);
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        {
            int pixel = (y * width + x) * 3;
            coordinates.Data[pixel] = x;
            coordinates.Data[pixel + 1] = y;
            coordinates.Data[pixel + 2] = 1;
        }
        var lens = key.EmbeddedLens;
        // A chromatic-aberration map can warp the three channels differently.
        if (lens?.Warp is { Length: >= 3 } warp) lens = lens with { Warp = [warp[1]] };
        coordinates = LensCorrections.ApplyGeometry(coordinates, new FrameParams
        {
            EmbeddedLens = lens, Lensfun = key.Lensfun, LensfunEnabled = key.Lensfun is not null,
            DistortionK1 = key.Distortion,
        });
        coordinates = Geometry.ApplyOrientation(coordinates, key.Turns, key.FlipH, key.FlipV);
        coordinates = Geometry.ApplyRotation(coordinates, key.Rotation, fill: 0);
        if (key.Crop is { } crop) coordinates = Geometry.ApplyCrop(coordinates, crop);
        _coordinates = coordinates;
        Width = coordinates.Width;
        Height = coordinates.Height;
        _ids = new int[coordinates.PixelCount];
        Array.Fill(_ids, -1);
        for (int i = 0; i < _ids.Length; i++)
        {
            int pixel = i * 3;
            float valid = coordinates.Data[pixel + 2];
            if (valid < .5f) continue;
            double sx = Math.Clamp(coordinates.Data[pixel] / valid, 0, width - 1);
            double sy = Math.Clamp(coordinates.Data[pixel + 1] / valid, 0, height - 1);
            int id = SampleId(rawIds, width, height, sx, sy);
            if (id < 0) continue;
            _ids[i] = id;
            VisibleIds.Add(id);
        }
    }

    private static int SampleId(int[] ids, int width, int height, double x, double y)
    {
        int x0 = (int)x, y0 = (int)y;
        double bestWeight = .05;
        int bestId = -1;
        for (int sy = y0; sy <= Math.Min(y0 + 1, height - 1); sy++)
        for (int sx = x0; sx <= Math.Min(x0 + 1, width - 1); sx++)
        {
            double weight = (1 - Math.Abs(sx - x)) * (1 - Math.Abs(sy - y));
            int id = ids[sy * width + sx];
            if (id < 0 || weight <= bestWeight) continue;
            bestWeight = weight;
            bestId = id;
        }
        return bestId;
    }

    public DustMaskEditor.Brush? MakeBrush(Point normal, double radiusX, double radiusY)
    {
        var center = SourcePoint(normal);
        double left = Math.Max(0, normal.X - 1d / Width), right = Math.Min(1, normal.X + 1d / Width);
        double top = Math.Max(0, normal.Y - 1d / Height), bottom = Math.Min(1, normal.Y + 1d / Height);
        var horizontalStart = SourcePoint(new(left, normal.Y));
        var horizontalEnd = SourcePoint(new(right, normal.Y));
        var verticalStart = SourcePoint(new(normal.X, top));
        var verticalEnd = SourcePoint(new(normal.X, bottom));
        if (center is not { } p || horizontalStart is not { } h0 || horizontalEnd is not { } h1 ||
            verticalStart is not { } v0 || verticalEnd is not { } v1 || left == right || top == bottom) return null;
        return new(p.X, p.Y, (h1.X - h0.X) * radiusX / (right - left),
            (h1.Y - h0.Y) * radiusX / (right - left),
            (v1.X - v0.X) * radiusY / (bottom - top), (v1.Y - v0.Y) * radiusY / (bottom - top));
    }

    private Point? SourcePoint(Point normal)
    {
        if (normal.X is < 0 or > 1 || normal.Y is < 0 or > 1) return null;
        double x = Math.Clamp(normal.X * Width - .5, 0, Width - 1);
        double y = Math.Clamp(normal.Y * Height - .5, 0, Height - 1);
        double sx = 0, sy = 0, valid = 0;
        int x0 = (int)x, y0 = (int)y;
        for (int py = y0; py <= Math.Min(y0 + 1, Height - 1); py++)
        for (int px = x0; px <= Math.Min(x0 + 1, Width - 1); px++)
        {
            double weight = (1 - Math.Abs(px - x)) * (1 - Math.Abs(py - y));
            int index = (py * Width + px) * 3;
            sx += _coordinates.Data[index] * weight;
            sy += _coordinates.Data[index + 1] * weight;
            valid += _coordinates.Data[index + 2] * weight;
        }
        if (valid < .5) return null;
        return new(((_margin.X + (sx / valid + .5) / _rawWidth * _margin.W - _bounds.X) / _bounds.W) * _sourceWidth - .5,
            ((_margin.Y + (sy / valid + .5) / _rawHeight * _margin.H - _bounds.Y) / _bounds.H) * _sourceHeight - .5);
    }

    public byte[] MakeOverlay(DustMask mask)
    {
        var raw = DustMaskDisplay.Coverage(mask.RelativeTo(_margin), _rawWidth, _rawHeight);
        var rgba = new byte[checked(_ids.Length * 4)];
        for (int i = 0; i < _ids.Length; i++)
        {
            int pixel = i * 3;
            float valid = _coordinates.Data[pixel + 2];
            if (valid < .5f) continue;
            double x = Math.Clamp(_coordinates.Data[pixel] / valid, 0, _rawWidth - 1);
            double y = Math.Clamp(_coordinates.Data[pixel + 1] / valid, 0, _rawHeight - 1);
            double coverage = 0;
            int x0 = (int)x, y0 = (int)y;
            for (int py = y0; py <= Math.Min(y0 + 1, _rawHeight - 1); py++)
            for (int px = x0; px <= Math.Min(x0 + 1, _rawWidth - 1); px++)
                coverage += raw[py * _rawWidth + px] * (1 - Math.Abs(px - x)) * (1 - Math.Abs(py - y));
            DustMaskDisplay.WritePixel(rgba, i, coverage);
        }
        return rgba;
    }
}
