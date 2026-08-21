using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using OpenRevelare.Core;
using OpenRevelare.Gui.Interop;
using SkiaSharp;

namespace OpenRevelare.Gui.Controls;

/// <summary>
/// An <see cref="Image"/>-shaped control that draws its bitmap through Skia WITH the bitmap's
/// colour space declared, instead of handing the compositor an untagged buffer.
///
/// THE PROBLEM. Avalonia cannot express a bitmap's colour space — every <c>SKImageInfo</c> and
/// every surface it builds omits the parameter (AvaloniaUI/Avalonia#8450, #14599; open and
/// unassigned since 2022, still true on master). So a preview buffer reached the screen untagged
/// and was read as sRGB by whoever consumed it. For an sRGB roll that guess is right; for a
/// Display P3 or Adobe RGB roll it is wrong, and the preview no longer agreed with the export it
/// was supposed to be previewing. macOS made it worst and most visible: its compositor really
/// does act on "untagged means sRGB" and converts to the panel profile, so P3 numbers were read
/// as sRGB and then expanded AGAIN — every P3 roll came out oversaturated on every Mac, while
/// Windows and Linux (which pass numbers through untouched) showed something different again.
/// Three platforms, three pictures, none of them the exported file.
///
/// WHAT THIS DOES. Leases the real <see cref="SKCanvas"/> out of the drawing context and draws an
/// <see cref="SKImage"/> that carries the roll's space (see <see cref="SkiaColorSpace"/>). Skia
/// then performs the conversion into the destination, which is the step nobody was doing.
///
/// WHAT IT STILL CANNOT DO. Avalonia's destination surface is itself untagged, i.e. sRGB, so the
/// conversion lands in sRGB and colour outside sRGB's gamut cannot be shown on a wide-gamut panel.
/// That ceiling belongs to the framework and cannot be lifted from a control. The gain here is
/// that the preview stops being WRONG — in-gamut colour is placed correctly, the three platforms
/// agree with each other, and what is on screen matches what gets exported. Anyone needing to see
/// beyond sRGB has to judge from the exported file, which does carry the right profile.
///
/// FALLBACK. Where the Skia lease is unavailable (a non-Skia backend), this degrades to the plain
/// untagged draw — exactly the old behaviour, which is the honest fallback rather than a guess.
/// </summary>
public class ColorManagedImage : Control
{
    /// <summary>The bitmap to draw. Same role as <see cref="Image.Source"/>.</summary>
    public static readonly StyledProperty<IImage?> SourceProperty =
        AvaloniaProperty.Register<ColorManagedImage, IImage?>(nameof(Source));

    /// <summary>How the bitmap fills the control. Only Uniform is used today, but the property
    /// exists so this is a drop-in for the <see cref="Image"/> it replaces.</summary>
    public static readonly StyledProperty<Stretch> StretchProperty =
        AvaloniaProperty.Register<ColorManagedImage, Stretch>(nameof(Stretch), Stretch.Uniform);

    public IImage? Source
    {
        get => GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    public Stretch Stretch
    {
        get => GetValue(StretchProperty);
        set => SetValue(StretchProperty, value);
    }

    static ColorManagedImage()
    {
        AffectsRender<ColorManagedImage>(SourceProperty, StretchProperty);
        AffectsMeasure<ColorManagedImage>(SourceProperty, StretchProperty);
    }

    /// <summary>
    /// The colour space a bitmap's pixels are in, remembered ALONGSIDE the bitmap rather than
    /// inside it, because Avalonia's type has nowhere to put it.
    ///
    /// A <see cref="ConditionalWeakTable{TKey,TValue}"/> so the association dies with the bitmap:
    /// previews are produced and retired continuously (see MainViewModel's retirement queue), and
    /// a plain dictionary keyed on bitmaps would pin every frame ever rendered.
    /// </summary>
    private static readonly ConditionalWeakTable<IImage, Box> Spaces = new();

    private sealed class Box
    {
        public ColorSpaceDef Space;
    }

    /// <summary>Declares what colour space <paramref name="image"/>'s pixels are encoded in.
    /// Call this wherever a bitmap is built; an undeclared bitmap is drawn as sRGB, which is the
    /// same assumption everything made before and is correct for the sRGB rolls.
    ///
    /// Named Declare rather than Tag because <see cref="Control.Tag"/> already exists and means
    /// something unrelated.</summary>
    public static void Declare(IImage image, ColorSpaceDef space)
    {
        Spaces.Remove(image);
        Spaces.Add(image, new Box { Space = space });
    }

    /// <summary>The declared space, or sRGB when nothing was declared.</summary>
    public static ColorSpaceDef SpaceOf(IImage image)
        => Spaces.TryGetValue(image, out Box? b) ? b.Space : ColorSpaces.Srgb;

    protected override Size MeasureOverride(Size availableSize)
    {
        IImage? src = Source;
        return src is null ? default : Stretch.CalculateSize(availableSize, src.Size, StretchDirection.Both);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        IImage? src = Source;
        return src is null ? default : Stretch.CalculateSize(finalSize, src.Size, StretchDirection.Both);
    }

    public override void Render(DrawingContext context)
    {
        if (Source is not Bitmap bmp) return;

        Rect dest = DestRect(bmp.Size, Bounds.Size);
        if (dest.Width <= 0 || dest.Height <= 0) return;

        context.Custom(new Op(dest, bmp, SpaceOf(bmp)));
    }

    /// <summary>Where the bitmap lands inside the control, honouring <see cref="Stretch"/>.
    /// Uniform centres it, which is what the letterbox maths elsewhere in the window assumes.</summary>
    private Rect DestRect(Size image, Size bounds)
    {
        Size scaled = Stretch.CalculateSize(bounds, image, StretchDirection.Both);
        return new Rect(
            (bounds.Width - scaled.Width) / 2.0,
            (bounds.Height - scaled.Height) / 2.0,
            scaled.Width, scaled.Height);
    }

    /// <summary>
    /// The draw itself. Runs on the render thread, so it may touch neither the control nor the
    /// bitmap's Avalonia wrapper — everything it needs is captured at construction.
    /// </summary>
    private sealed class Op : ICustomDrawOperation
    {
        private readonly Rect _dest;
        private readonly Bitmap _bmp;
        private readonly ColorSpaceDef _space;

        public Op(Rect dest, Bitmap bmp, ColorSpaceDef space)
        {
            _dest = dest;
            _bmp = bmp;
            _space = space;
        }

        public Rect Bounds => _dest;

        public bool HitTest(Point p) => _dest.Contains(p);

        public bool Equals(ICustomDrawOperation? other) => false;

        public void Dispose() { }

        public void Render(ImmediateDrawingContext context)
        {
            var lease = context.TryGetFeature<ISkiaSharpApiLeaseFeature>();
            if (lease is null)
            {
                // Non-Skia backend: draw untagged, i.e. exactly what happened before this class
                // existed. Wrong for wide-gamut rolls, but no worse, and it still shows a picture.
                context.DrawBitmap(_bmp, new Rect(_bmp.Size), _dest);
                return;
            }

            using ISkiaSharpApiLease l = lease.Lease();
            SKCanvas canvas = l.SkCanvas;

            using SKImage? img = Snapshot(_bmp, _space);
            if (img is null)
            {
                context.DrawBitmap(_bmp, new Rect(_bmp.Size), _dest);
                return;
            }

            var dst = new SKRect((float)_dest.X, (float)_dest.Y,
                                 (float)_dest.Right, (float)_dest.Bottom);

            // High-quality downscale: the preview is routinely shown well below 1:1, and the
            // nearest-neighbour default aliases film grain into moiré.
            using var paint = new SKPaint { FilterQuality = SKFilterQuality.High, IsAntialias = true };
            canvas.DrawImage(img, dst, paint);
        }

        /// <summary>
        /// Copies the Avalonia bitmap's pixels into an <see cref="SKImage"/> that DECLARES
        /// <paramref name="space"/>.
        ///
        /// A copy, because Avalonia owns the original's memory and the render thread must not
        /// outlive its lock. That is one full-frame copy per paint — acceptable for a preview at
        /// window size, and the price of a colour space Avalonia will not carry itself.
        /// </summary>
        private static SKImage? Snapshot(Bitmap bmp, ColorSpaceDef space)
        {
            var size = new PixelSize((int)bmp.Size.Width, (int)bmp.Size.Height);
            if (size.Width <= 0 || size.Height <= 0) return null;

            var info = new SKImageInfo(size.Width, size.Height,
                                       SKColorType.Bgra8888, SKAlphaType.Unpremul,
                                       SkiaColorSpace.For(space));

            var pixels = new SKBitmap();
            if (!pixels.TryAllocPixels(info))
            {
                pixels.Dispose();
                return null;
            }

            bmp.CopyPixels(new PixelRect(0, 0, size.Width, size.Height),
                           pixels.GetPixels(), pixels.ByteCount, pixels.RowBytes);

            // FromBitmap takes ownership of the pixel data, so the SKBitmap is not disposed here.
            return SKImage.FromBitmap(pixels);
        }
    }
}
