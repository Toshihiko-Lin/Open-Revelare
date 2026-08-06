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
}
