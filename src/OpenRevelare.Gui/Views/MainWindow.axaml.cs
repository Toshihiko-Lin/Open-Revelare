using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Controls.Shapes;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using OpenRevelare.Core;
using OpenRevelare.Gui.Controls;
using OpenRevelare.Gui.Services;
using OpenRevelare.Gui.ViewModels;

namespace OpenRevelare.Gui.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Library.OpenRequested += OnLibraryOpenRequested;
        Library.NewRollRequested += ImportNewRollAsync;
        Curves.CurvesChanged += (_, _) => PushCurves();
        Curves.PreserveHueChanged += (_, _) => PushCurves();
        // Drag mode for every parameter control in the window. SliderRow's events bubble, so one
        // subscription here covers all of them — including rows added later.
        AddHandler(SliderRow.InteractionStartedEvent, (_, _) => Vm?.BeginInteractive());
        AddHandler(SliderRow.InteractionEndedEvent, (_, _) => Vm?.EndInteractive());
        Curves.InteractionStarted += (_, _) => Vm?.BeginInteractive();
        Curves.InteractionEnded += (_, _) => Vm?.EndInteractive();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is MainViewModel vm)
            {
                vm.AskRelinkFolder = AskRelinkFolderAsync;
                vm.PropertyChanged += (_, args) =>
                {
                    if (args.PropertyName == nameof(MainViewModel.Histogram))
                        Curves.SetHistogram(vm.Histogram);
                    else if (args.PropertyName == nameof(MainViewModel.PreviewImage))
                        OnPreviewBitmapChanged();
                    else if (args.PropertyName == nameof(MainViewModel.Patch))
                        UpdatePatchLayout();
                };
                vm.FrameParamsLoaded += p =>
                {
                    Curves.SetAll(p.CurvePointsM, p.CurvePointsR, p.CurvePointsG, p.CurvePointsB, p.CurvePreserveHue);
                    // The crop frame belongs to the picture it was placed on. Carrying it to the
                    // next frame leaves a box sitting over a composition nobody chose it for, and
                    // the arriving frame's own crop stays suppressed while it does.
                    if (_mode == SampleMode.Crop) ExitMode();
                    ResetZoom();   // a new frame → back to fit
                };
                vm.RollImported += OnRollImported;
            }
        };
        // Onboarding first, then the delayed update check — the notice must not open on top of
        // the first-run help, which is itself modal.
        Opened += async (_, _) =>
        {
            await MaybeShowOnboarding();
            StartBackgroundUpdateCheck();
        };

        // Zoom/pan transform on the whole preview stack (image + overlay move together).
        ZoomGrid.RenderTransform = new TransformGroup { Children = { _scale, _translate } };

        // A resize changes the letterbox and the fit scale, so the pan clamp and the zoom
        // percentage both go stale unless the transform is recomputed.
        ViewPort.SizeChanged += (_, _) => ApplyTransform();

        // If the pointer capture is stolen (another window, a touch cancel), the drag flags would
        // otherwise stay stuck true and the next click would behave as a continued drag.
        Overlay.PointerCaptureLost += (_, _) =>
        {
            _panning = false;
            _dragging = false;
            SelRect.IsVisible = false;
            UpdatePanCursor();
        };

        // Right-clicking a thumbnail must first make it the current frame — otherwise the
        // context menu (which acts on the selection) would silently operate on whatever was
        // selected before, which is exactly the kind of surprise that loses an edit.
        FilmStrip.AddHandler(PointerPressedEvent, OnFilmStripPointerPressed,
                             RoutingStrategies.Tunnel);

        // Crop handles: eight identical squares, built here rather than in XAML because they
        // are positioned entirely from code anyway and eight near-duplicate elements in the
        // markup would only be noise.
        for (int i = 0; i < _cropHandleShapes.Length; i++)
        {
            var r = new Rectangle
            {
                IsVisible = false,
                Fill = new SolidColorBrush(Color.Parse("#F2F5F7")),
                Stroke = new SolidColorBrush(Color.Parse("#1C1E20")),
                StrokeThickness = 1,
            };
            _cropHandleShapes[i] = r;
            Overlay.Children.Add(r);
        }

        SyncViewerBgChecks();
    }

    /// <summary>First-ever launch: show the help/onboarding once (persisted by a marker file).</summary>
    private async Task MaybeShowOnboarding()
    {
        try
        {
            string dir = Services.Settings.DataDir;
            string marker = System.IO.Path.Combine(dir, "onboarded");
            if (System.IO.File.Exists(marker)) return;
            System.IO.Directory.CreateDirectory(dir);
            await System.IO.File.WriteAllTextAsync(marker, "1");
            await InfoDialog.Help().ShowDialog(this);
        }
        catch { /* onboarding is best-effort */ }
    }

    private MainViewModel? Vm => DataContext as MainViewModel;

    private void PushCurves() =>
        Vm?.SetCurves(Curves.GetChannel(0), Curves.GetChannel(1),
                      Curves.GetChannel(2), Curves.GetChannel(3), Curves.PreserveHue);

    // ── ROI sampling / crop (checkable toggle → drag a rubber-band on the preview) ─
    // Mirrors Python's role="sampling" checkable buttons: clicking a tool arms a
    // persistent sampling mode (the button stays lit) until you draw a rect or press
    // Esc. The tools are mutually exclusive — arming one disarms the others.
    private enum SampleMode
    {
        None, FilmBase, WbOffset, WbHigh, DMax, ScanEv, GreyPoint, Black, White, Crop,
        StraightenH, StraightenV,
    }

    /// <summary>The straighten tools drag a LINE, not a rect — they read an angle, not a region.</summary>
    private static bool IsLineMode(SampleMode m) => m is SampleMode.StraightenH or SampleMode.StraightenV;
    private SampleMode _mode = SampleMode.None;
    private bool _negativeShown;
    private Point _dragStart;
    private bool _dragging;

    // Crop aspect presets (index-aligned with CropPresetCombo); null = free / no lock.
    private static readonly double?[] CropAspects =
    {
        null, 3.0 / 2, 37.0 / 35, 3.0 / 4, 65.0 / 24, 4.0 / 3, 1.0, 7.0 / 6, 3.0 / 2, 2.0 / 1,
    };
    private double? _cropAspect;   // locked crop width/height ratio (screen w:h)

    // ── Editable crop frame ─────────────────────────────────────────────────────
    //
    // Picking a preset no longer commits a crop outright. It puts a FRAME on the picture that
    // can be dragged and resized — you position the photo inside the format rather than accept
    // whatever a centred rectangle happened to catch — and the crop is applied only on confirm.
    // Ported from the source's crop overlay (preview_widget.py::_crop_apply_drag), including
    // its anchoring rule: the corner opposite the handle you grabbed stays put.
    //
    // While this is up the preview renders UNCROPPED (Vm.CropEditing), so the frame is drawn in
    // the same space the rect is stored in and you can see what is being excluded.
    private (double X, double Y, double W, double H)? _cropDraft;
    private string? _cropHandle;                 // tl t tr r br b bl l | move | new
    private Point _cropDragStartNorm;
    private (double X, double Y, double W, double H) _cropDragStartRect;
    private const double HandleScreenSize = 10.0;
    private const double HandleGrabTol = 12.0;   // screen px, as in the source
    private static readonly string[] HandleIds = { "tl", "t", "tr", "r", "br", "b", "bl", "l" };
    private readonly Rectangle[] _cropHandleShapes = new Rectangle[8];

    // ── Zoom / pan (visual transform on the whole preview stack; sampling math is
    // unaffected because GetPosition(Overlay) returns pre-transform local coords) ──
    private double _zoom = 1.0;          // 1.0 = fit-to-window
    private Point _pan;                  // translate in viewport space
    private bool _panning;
    private Point _panLast;
    private readonly ScaleTransform _scale = new(1, 1);
    private readonly TranslateTransform _translate = new(0, 0);

    /// <summary>
    /// The smallest <see cref="_zoom"/> allowed right now.
    ///
    /// _zoom multiplies the FIT scale, so 1.0 means "fit the window" and that is normally the
    /// floor — zooming out past fit would only shrink the photo into a sea of background. But
    /// fit is an UPSCALE whenever the bitmap is smaller than the viewport, and that is exactly
    /// what a crop produces: halve a 1600 px preview and the 800 px result gets blown up to
    /// fill. In that state true 1:1 lives BELOW fit, at 1/FitScale — so a hard floor of 1.0 made
    /// 实际大小 a dead button. It computed a target of 1/fit &lt; 1, the clamp put it straight
    /// back to the current zoom, and nothing moved.
    /// </summary>
    private double MinZoom()
    {
        double fit = FitScale();
        return fit > 1.0 ? 1.0 / fit : 1.0;
    }

    private double FitScale()
    {
        if (PreviewImg.Source is not Bitmap bmp) return 1;
        double iw = bmp.PixelSize.Width, ih = bmp.PixelSize.Height;
        double vw = ViewPort.Bounds.Width, vh = ViewPort.Bounds.Height;
        return iw <= 0 || ih <= 0 || vw <= 0 || vh <= 0 ? 1 : Math.Min(vw / iw, vh / ih);
    }

    /// <summary>The displayed bitmap's rect inside ZoomGrid BEFORE the render transform — the
    /// Uniform letterbox, i.e. the same centring <see cref="ToNormalisedRect"/> undoes.</summary>
    private Rect? LetterboxRect()
    {
        if (PreviewImg.Source is not Bitmap bmp) return null;
        double iw = bmp.PixelSize.Width, ih = bmp.PixelSize.Height;
        double vw = ViewPort.Bounds.Width, vh = ViewPort.Bounds.Height;
        if (iw <= 0 || ih <= 0 || vw <= 0 || vh <= 0) return null;
        double s = Math.Min(vw / iw, vh / ih);
        return new Rect((vw - iw * s) / 2, (vh - ih * s) / 2, iw * s, ih * s);
    }

    /// <summary>Keep the photo glued to the viewport: zoomed in, it cannot be dragged past its own
    /// edges (no empty gutters); at or below fit it stays centred. Without this a drag could fling
    /// the image off-screen with no way back except F.</summary>
    private void ClampPan()
    {
        if (LetterboxRect() is not { } box) { _pan = default; return; }
        _pan = new Point(ClampAxis(_pan.X, box.X, box.Width, ViewPort.Bounds.Width),
                         ClampAxis(_pan.Y, box.Y, box.Height, ViewPort.Bounds.Height));

        double ClampAxis(double p, double off, double len, double view)
        {
            double scaled = len * _zoom;
            if (scaled <= view) return (view - scaled) / 2 - off * _zoom;      // fits → centre
            return Math.Clamp(p, view - (off + len) * _zoom, -off * _zoom);    // overflows → no gaps
        }
    }

    /// <summary>True when a drag would actually move something — at fit the image is fully
    /// visible, so panning is a no-op and the cursor should not promise otherwise.</summary>
    private bool CanPan() => Vm?.HasImage == true && _zoom > 1.0;

    /// <summary>Pan affordance. The gesture used to be right/middle-drag only with no cursor
    /// change at all, which is why it read as "dragging doesn't work".</summary>
    // Cached: UpdatePanCursor runs from ApplyTransform, i.e. on every pointer-move of a drag.
    private static readonly Cursor GrabCursor = new(StandardCursorType.SizeAll);
    private static readonly Cursor HandCursor = new(StandardCursorType.Hand);

    private void UpdatePanCursor()
    {
        if (_mode != SampleMode.None) return;   // a sampling mode owns the cursor (cross-hair)
        Cursor want = _panning ? GrabCursor : CanPan() ? HandCursor : Cursor.Default;
        if (!ReferenceEquals(Overlay.Cursor, want)) Overlay.Cursor = want;
    }

    private void ApplyTransform()
    {
        double floor = MinZoom();
        if (_zoom < floor) _zoom = floor;
        ClampPan();
        _scale.ScaleX = _scale.ScaleY = _zoom;
        _translate.X = _pan.X; _translate.Y = _pan.Y;
        ZoomLabel.Text = $"{_zoom * FitScale() * 100:F0}%";   // true on-screen pixel ratio
        UpdatePanCursor();
        UpdatePatchLayout();
        RenderCropFrame();
    }

    private void ResetZoom() { _zoom = 1.0; _pan = default; ApplyTransform(); Vm?.ClearSharpPatch(); }

    // ── Crop frame: coordinate helpers ──────────────────────────────────────────
    /// <summary>Overlay point → normalised image coords (undoing the Uniform letterbox).</summary>
    private Point? NormFromOverlay(Point p)
    {
        if (LetterboxRect() is not { } b || b.Width <= 0 || b.Height <= 0) return null;
        return new Point((p.X - b.X) / b.Width, (p.Y - b.Y) / b.Height);
    }

    /// <summary>The crop's locked ratio expressed in NORMALISED coords. The stored rect is
    /// normalised over the frame, so a screen ratio r needs nw/nh = r · (frameH / frameW).</summary>
    private double? NormAspect()
    {
        if (_cropAspect is not double r) return null;
        if (Vm?.CropFrameSize is not var (fw, fh) || fw <= 0 || fh <= 0) return null;
        return r * fh / fw;
    }

    /// <summary>Start a draft: the largest centred rectangle of the locked ratio at 90% of the
    /// frame, or 90% of the frame itself when free. 90% rather than 100% so every handle is
    /// grabbable instead of sitting on the image edge.</summary>
    private void BeginCropDraft()
    {
        double cw = 0.9, ch = 0.9;
        if (NormAspect() is double na)
        {
            if (cw / ch > na) cw = ch * na; else ch = cw / na;
        }
        _cropDraft = ((1 - cw) / 2, (1 - ch) / 2, cw, ch);
        RenderCropFrame();
    }

    /// <summary>Which handle the cursor is over. Tolerance is in SCREEN pixels, so grabbing is
    /// equally easy at any zoom — hence the division by the zoom factor.</summary>
    private string? CropHitHandle(Point p)
    {
        if (_cropDraft is not { } c || LetterboxRect() is not { } b) return null;
        double left = b.X + c.X * b.Width, right = b.X + (c.X + c.W) * b.Width;
        double top = b.Y + c.Y * b.Height, bottom = b.Y + (c.Y + c.H) * b.Height;
        double tol = HandleGrabTol / Math.Max(_zoom, 1e-6);
        bool nl = Math.Abs(p.X - left) <= tol, nr = Math.Abs(p.X - right) <= tol;
        bool nt = Math.Abs(p.Y - top) <= tol, nb = Math.Abs(p.Y - bottom) <= tol;
        bool inX = p.X >= left - tol && p.X <= right + tol;
        bool inY = p.Y >= top - tol && p.Y <= bottom + tol;
        if (nt && nl) return "tl";
        if (nt && nr) return "tr";
        if (nb && nl) return "bl";
        if (nb && nr) return "br";
        if (nt && inX) return "t";
        if (nb && inX) return "b";
        if (nl && inY) return "l";
        if (nr && inY) return "r";
        if (p.X > left && p.X < right && p.Y > top && p.Y < bottom) return "move";
        return null;
    }

    private static StandardCursorType CursorForHandle(string? h) => h switch
    {
        "tl" or "br" => StandardCursorType.TopLeftCorner,
        "tr" or "bl" => StandardCursorType.TopRightCorner,
        "t" or "b" => StandardCursorType.SizeNorthSouth,
        "l" or "r" => StandardCursorType.SizeWestEast,
        "move" => StandardCursorType.SizeAll,
        _ => StandardCursorType.Cross,
    };

    /// <summary>Update the draft from the active handle drag — port of _crop_apply_drag.</summary>
    private void ApplyCropDrag(Point m)
    {
        double mx = Math.Clamp(m.X, 0, 1), my = Math.Clamp(m.Y, 0, 1);
        var (x, y, w, h) = _cropDragStartRect;
        double x2 = x + w, y2 = y + h;
        double? na = NormAspect();

        if (_cropHandle == "new")
        {
            double sx = _cropDragStartNorm.X, sy = _cropDragStartNorm.Y;
            double nw = Math.Abs(mx - sx), nh = Math.Abs(my - sy);
            if (na is double a)
            {
                if (nw / Math.Max(nh, 1e-6) > a) nh = nw / a; else nw = nh * a;
            }
            _cropDraft = (mx < sx ? sx - nw : sx, my < sy ? sy - nh : sy, nw, nh);
            return;
        }
        if (_cropHandle == "move")
        {
            double dx = mx - _cropDragStartNorm.X, dy = my - _cropDragStartNorm.Y;
            _cropDraft = (Math.Clamp(x + dx, 0, 1 - w), Math.Clamp(y + dy, 0, 1 - h), w, h);
            return;
        }

        // Edge / corner: the touched edges move, the opposite ones stay.
        string hd = _cropHandle ?? "";
        if (hd.Contains('l')) x = Math.Min(mx, x2 - 0.01);
        if (hd.Contains('r')) x2 = Math.Max(mx, x + 0.01);
        if (hd.Contains('t')) y = Math.Min(my, y2 - 0.01);
        if (hd.Contains('b')) y2 = Math.Max(my, y + 0.01);
        double rw = x2 - x, rh = y2 - y;
        if (na is double asp)
        {
            bool corner = hd is "tl" or "tr" or "bl" or "br";
            if (corner) { if (rw / Math.Max(rh, 1e-6) > asp) rh = rw / asp; else rw = rh * asp; }
            else if (hd is "l" or "r") rh = rw / asp;
            else rw = rh * asp;
            // Anchor the corner OPPOSITE the dragged edge.
            _cropDraft = (hd.Contains('l') ? x2 - rw : x, hd.Contains('t') ? y2 - rh : y, rw, rh);
            return;
        }
        _cropDraft = (x, y, rw, rh);
    }

    /// <summary>Draw the frame, the dim outside it, the thirds and the handles. Stroke and
    /// handle size are divided by the zoom so they stay constant on screen — this all lives
    /// inside ZoomGrid and is scaled by the shared render transform.</summary>
    private void RenderCropFrame()
    {
        bool show = _mode == SampleMode.Crop && _cropDraft is not null && LetterboxRect() is not null;
        foreach (Control c in new Control[] { CropDimT, CropDimB, CropDimL, CropDimR, CropFrame,
                                              CropV1, CropV2, CropH1, CropH2 })
            c.IsVisible = show;
        foreach (Rectangle r in _cropHandleShapes) if (r is not null) r.IsVisible = show;
        if (!show) return;

        var b = LetterboxRect()!.Value;
        var (cx, cy, cw, ch) = _cropDraft!.Value;
        double L = b.X + cx * b.Width, T = b.Y + cy * b.Height;
        double W = cw * b.Width, H = ch * b.Height;
        double inv = 1.0 / Math.Max(_zoom, 1e-6);

        void Put(Rectangle r, double x, double y, double w, double h)
        {
            Canvas.SetLeft(r, x); Canvas.SetTop(r, y);
            r.Width = Math.Max(0, w); r.Height = Math.Max(0, h);
        }
        Put(CropDimT, b.X, b.Y, b.Width, T - b.Y);
        Put(CropDimB, b.X, T + H, b.Width, b.Y + b.Height - (T + H));
        Put(CropDimL, b.X, T, L - b.X, H);
        Put(CropDimR, L + W, T, b.X + b.Width - (L + W), H);
        Put(CropFrame, L, T, W, H);
        CropFrame.StrokeThickness = 1.5 * inv;

        void Line(Line ln, double x1, double y1, double x2, double y2)
        {
            ln.StartPoint = new Point(x1, y1); ln.EndPoint = new Point(x2, y2);
            ln.StrokeThickness = 1.0 * inv;
        }
        Line(CropV1, L + W / 3, T, L + W / 3, T + H);
        Line(CropV2, L + 2 * W / 3, T, L + 2 * W / 3, T + H);
        Line(CropH1, L, T + H / 3, L + W, T + H / 3);
        Line(CropH2, L, T + 2 * H / 3, L + W, T + 2 * H / 3);

        double hs = HandleScreenSize * inv, half = hs / 2;
        var pos = new (double X, double Y)[]
        {
            (L, T), (L + W / 2, T), (L + W, T), (L + W, T + H / 2),
            (L + W, T + H), (L + W / 2, T + H), (L, T + H), (L, T + H / 2),
        };
        for (int i = 0; i < 8; i++)
        {
            _cropHandleShapes[i].StrokeThickness = 1.0 * inv;
            Put(_cropHandleShapes[i], pos[i].X - half, pos[i].Y - half, hs, hs);
        }
    }

    private void CommitCrop()
    {
        if (_cropDraft is { } c && Vm is not null && c.W > 0.005 && c.H > 0.005)
        {
            Vm.CropEditing = false;      // before SetCrop, so the render that follows is cropped
            Vm.SetCrop(c);
        }
        ExitMode();                      // drops the draft
    }

    private void CancelCropDraft() => ExitMode();   // ExitMode drops the draft and un-suppresses the crop

    // ── Sharp patch (local full-resolution zoom) ────────────────────────────────
    //
    // Past this much zoom the preview's own pixels are visibly the limit, so ask the VM to
    // re-render the visible slice from the original resolution. 1.5× matches the source's
    // _HIRES_THRESHOLD. The VM declines requests that would cost too much, so this threshold
    // only has to be "worth looking at", not "cheap".
    private const double SharpPatchThreshold = 1.5;
    private const double SharpPatchMargin = 0.10;   // pan buffer, as in visible_roi_norm

    /// <summary>The image region currently visible, normalised, expanded by a pan margin.
    /// Screen → ZoomGrid is (s - pan)/zoom; ZoomGrid → normalised divides out the letterbox.</summary>
    private RegionRender.Roi? VisibleRoiNorm()
    {
        if (LetterboxRect() is not { } box || box.Width <= 0 || box.Height <= 0) return null;
        double x0 = ((0 - _pan.X) / _zoom - box.X) / box.Width;
        double y0 = ((0 - _pan.Y) / _zoom - box.Y) / box.Height;
        double x1 = ((ViewPort.Bounds.Width - _pan.X) / _zoom - box.X) / box.Width;
        double y1 = ((ViewPort.Bounds.Height - _pan.Y) / _zoom - box.Y) / box.Height;
        double mw = (x1 - x0) * SharpPatchMargin, mh = (y1 - y0) * SharpPatchMargin;
        x0 = Math.Clamp(x0 - mw, 0, 1); y0 = Math.Clamp(y0 - mh, 0, 1);
        x1 = Math.Clamp(x1 + mw, 0, 1); y1 = Math.Clamp(y1 + mh, 0, 1);
        if (x1 - x0 <= 0 || y1 - y0 <= 0) return null;
        return new RegionRender.Roi(x0, y0, x1 - x0, y1 - y0);
    }

    /// <summary>Ask for (or drop) the sharp patch for the current view.</summary>
    private void SyncSharpPatch()
    {
        if (Vm is not { HasImage: true } vm) return;
        if (_zoom <= SharpPatchThreshold) { vm.ClearSharpPatch(); return; }
        if (VisibleRoiNorm() is { } roi) _ = vm.RequestSharpPatchAsync(roi);
    }

    /// <summary>Place the patch over the sub-rectangle of the letterbox it covers. Runs inside
    /// ZoomGrid, so the shared render transform then scales and pans it with the photo.</summary>
    private void UpdatePatchLayout()
    {
        MainViewModel.SharpPatch? patch = Vm?.Patch;
        if (patch is null || LetterboxRect() is not { } box)
        {
            PatchImg.Source = null;
            PatchImg.IsVisible = false;
            return;
        }
        PatchImg.Source = patch.Image;
        PatchImg.IsVisible = true;
        PatchImg.Width = patch.W * box.Width;
        PatchImg.Height = patch.H * box.Height;
        Canvas.SetLeft(PatchImg, box.X + patch.X * box.Width);
        Canvas.SetTop(PatchImg, box.Y + patch.Y * box.Height);
    }

    /// <summary>
    /// Re-apply the transform when the rendered bitmap changes SIZE.
    ///
    /// Applying a crop swaps in a smaller bitmap, which moves the fit scale, the zoom floor and
    /// the pan bounds all at once — but nothing recomputed them, so the % readout kept showing
    /// the old frame's number and the pan clamp stayed sized for the uncropped image until the
    /// next wheel/resize nudged ApplyTransform.
    ///
    /// Gated on the SIZE, not on the reference: a render lands a fresh bitmap on every debounce
    /// tick and every drag step, and re-running this per drag frame would rebuild the label
    /// string a hundred times a second for a number that has not moved.
    ///
    /// The size is read off the VIEW MODEL, and the recompute is posted rather than run inline,
    /// because this runs from PropertyChanged — the same event the Source binding listens to,
    /// with no defined ordering between the two subscribers. Reading PreviewImg.Source here (or
    /// letting FitScale read it) can therefore still see the OUTGOING bitmap. Posting lets the
    /// binding land first.
    /// </summary>
    private PixelSize _lastPreviewSize;

    private void OnPreviewBitmapChanged()
    {
        PixelSize size = Vm?.PreviewImage?.PixelSize ?? default;
        if (size != _lastPreviewSize)
        {
            _lastPreviewSize = size;
            Dispatcher.UIThread.Post(ApplyTransform, DispatcherPriority.Background);
        }
        // Every render invalidates the patch (the VM drops it), so re-ask for the new
        // parameters if the view is still zoomed in far enough to want one.
        Dispatcher.UIThread.Post(SyncSharpPatch, DispatcherPriority.Background);
    }

    private void OnFitClick(object? sender, RoutedEventArgs e) => ResetZoom();

    private void OnActualSizeClick(object? sender, RoutedEventArgs e)
    {
        double fit = FitScale();
        if (fit <= 0) return;
        // 1:1 with the displayed bitmap. Floor is MinZoom(), not 1.0, so this still works on a
        // cropped frame that fit has upscaled — see MinZoom.
        double target = Math.Clamp(1.0 / fit, MinZoom(), 40.0);
        // Zoom about the viewport centre.
        Point c = new(ViewPort.Bounds.Width / 2, ViewPort.Bounds.Height / 2);
        double factor = target / _zoom;
        _pan = new Point(c.X - (c.X - _pan.X) * factor, c.Y - (c.Y - _pan.Y) * factor);
        _zoom = target;
        ApplyTransform();
        SyncSharpPatch();
    }

    private void OnViewportWheel(object? sender, PointerWheelEventArgs e)
    {
        if (Vm?.HasImage != true) return;
        Point cur = e.GetPosition(ViewPort);
        double newZoom = Math.Clamp(_zoom * (e.Delta.Y > 0 ? 1.15 : 1 / 1.15), MinZoom(), 40.0);
        double factor = newZoom / _zoom;
        _pan = new Point(cur.X - (cur.X - _pan.X) * factor, cur.Y - (cur.Y - _pan.Y) * factor);
        _zoom = newZoom;
        ApplyTransform();
        SyncSharpPatch();
        e.Handled = true;
    }

    // ── Before/after compare (hold shows the un-graded positive) ─────────────────
    private void OnCompareToggle(object? sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton { IsChecked: { } on }) return;
        if (on) Vm?.ShowBeforeEdits(); else Vm?.ShowAfterEdits();
    }

    private ToggleButton[] AllToggles() => new[]
    {
        FilmBaseBtn, DMaxBtn, ScanEvBtn, WbOffsetBtn, WbHighBtn, GreyPointBtn, BlackBtn, WhiteBtn, CropBtn,
        StraightenHBtn, StraightenVBtn,
    };

    private void SetTogglesExcept(ToggleButton? keep)
    {
        foreach (var b in AllToggles())
            if (!ReferenceEquals(b, keep)) b.IsChecked = false;
    }

    private void EnterMode(SampleMode mode, string banner, bool useNegative)
    {
        // Arming another tool straight from the crop tool goes through here WITHOUT passing
        // ExitMode, so the crop-editing state had nothing to tear it down: CropEditing stayed
        // true and the preview kept hiding the applied crop for the rest of the session, with a
        // draft rectangle left over to be re-shown later over a different picture.
        bool leavingCrop = _mode == SampleMode.Crop && mode != SampleMode.Crop;
        if (_negativeShown) { Vm?.ShowPositiveView(); _negativeShown = false; }
        _mode = mode;
        if (leavingCrop) DiscardCropDraft();   // after _mode, so the frame actually hides
        Overlay.Cursor = new Cursor(StandardCursorType.Cross);
        BannerText.Text = banner;
        Banner.IsVisible = true;
        if (useNegative) { Vm?.ShowNegativeView(); _negativeShown = true; }

        bool crop = mode == SampleMode.Crop;
        CropApplyBtn.IsVisible = crop;
        CropCancelBtn.IsVisible = crop;
        Banner.IsHitTestVisible = crop;   // only the crop banner has anything to click
        if (crop)
        {
            // Show the whole frame while the crop is being placed, and start from the crop
            // already applied (so re-entering adjusts it) or a fresh centred draft.
            if (Vm is not null) Vm.CropEditing = true;
            _cropDraft = Vm?.CurrentCrop;
            if (_cropDraft is null) BeginCropDraft(); else RenderCropFrame();
            // Take focus off whatever armed the mode (the preset combo swallows Enter), so the
            // keyboard shortcuts actually reach OnKeyDown.
            Overlay.Focusable = true;
            Overlay.Focus();
        }
    }

    private void OnCropApplyClick(object? sender, RoutedEventArgs e) => CommitCrop();
    private void OnCropCancelClick(object? sender, RoutedEventArgs e) => CancelCropDraft();

    /// <summary>Drop the in-progress crop frame and hand the preview back to the applied crop.
    /// The draft is deliberately NOT kept for a later re-entry: it is normalised against one
    /// frame's picture, and re-showing it on the next one is how the box ends up sitting over
    /// something the user never selected. Re-entering the tool starts from the crop that is
    /// actually stored (<see cref="MainViewModel.CurrentCrop"/>).</summary>
    private void DiscardCropDraft()
    {
        _cropDraft = null;
        _cropHandle = null;
        if (Vm is not null) Vm.CropEditing = false;
        CropApplyBtn.IsVisible = false;
        CropCancelBtn.IsVisible = false;
        RenderCropFrame();          // gated on _mode, which the caller has already moved on
    }

    private void ExitMode()
    {
        _mode = SampleMode.None;
        DiscardCropDraft();
        Banner.IsHitTestVisible = false;
        UpdatePanCursor();          // back to hand/arrow depending on zoom
        SelLine.IsVisible = false;
        Banner.IsVisible = false;
        SetTogglesExcept(null);   // programmatic uncheck does not re-fire Click
        if (_negativeShown) { Vm?.ShowPositiveView(); _negativeShown = false; }
    }

    /// <summary>The panel toggle that represents a mode, so a mode armed from the MENU still lights
    /// its button (and so the menu path can find a button to un-light on exit).</summary>
    private ToggleButton? ToggleFor(SampleMode mode) => mode switch
    {
        SampleMode.FilmBase => FilmBaseBtn,
        SampleMode.WbOffset => WbOffsetBtn,
        SampleMode.WbHigh => WbHighBtn,
        SampleMode.DMax => DMaxBtn,
        SampleMode.ScanEv => ScanEvBtn,
        SampleMode.GreyPoint => GreyPointBtn,
        SampleMode.Black => BlackBtn,
        SampleMode.White => WhiteBtn,
        SampleMode.Crop => CropBtn,
        SampleMode.StraightenH => StraightenHBtn,
        SampleMode.StraightenV => StraightenVBtn,
        _ => null,
    };

    /// <summary>
    /// Shared arm/disarm logic for every checkable sampling tool, from either entry point.
    ///
    /// A ToggleButton arrives having ALREADY flipped its own IsChecked, so that flag is the
    /// request. A MenuItem is a plain command with no state of its own — it used to fall through
    /// the <c>IsChecked == true</c> test as "false" and disarm, which is why arming a tool from the
    /// menu did nothing. Treat it as a request to arm (or, if that tool is already armed, to
    /// toggle off), and mirror the result onto the panel button so the two views agree.
    /// </summary>
    private void ToggleSampling(object? sender, SampleMode mode, string banner, bool useNegative)
    {
        ToggleButton? btn = sender as ToggleButton ?? ToggleFor(mode);
        if (Vm?.HasImage != true) { if (btn is not null) btn.IsChecked = false; return; }

        bool arm = sender is ToggleButton tb ? tb.IsChecked == true : _mode != mode;
        if (!arm) { ExitMode(); return; }

        SetTogglesExcept(btn);
        if (btn is not null) btn.IsChecked = true;   // programmatic set does not re-fire Click
        EnterMode(mode, banner, useNegative);
    }

    /// <summary>
    /// Every gesture advertised by a menu item's InputGesture is bound here. Avalonia's
    /// <c>MenuItem.InputGesture</c> only *renders* the shortcut — it does not wire it — so a
    /// gesture shown in a menu and not listed below is a lie to the user.
    /// </summary>
    /// <summary>
    /// Last chance to persist the open roll. Synchronous on purpose: an awaited save would be
    /// racing the process exit, and the whole point of autosave is that closing the window is a
    /// safe way to stop working.
    /// </summary>
    protected override void OnClosing(WindowClosingEventArgs e)
    {
        Vm?.FlushRollNow();
        base.OnClosing(e);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        bool ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        bool shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        bool bare = e.KeyModifiers == KeyModifiers.None;
        bool img = Vm?.HasImage == true;

        // A bare letter must not fire while the user is typing in 卷注释 / a numeric field.
        bool typing = FocusManager?.GetFocusedElement() is TextBox;

        if (e.Key == Key.Escape && _mode == SampleMode.Crop) { CancelCropDraft(); e.Handled = true; }
        else if (e.Key == Key.Escape && _mode != SampleMode.None) { ExitMode(); e.Handled = true; }
        else if ((e.Key == Key.Enter || e.Key == Key.Return) && _mode == SampleMode.Crop)
        { CommitCrop(); e.Handled = true; }
        else if (bare && !typing && e.Key == Key.G) { OnLibraryModeClick(this, e); e.Handled = true; }
        else if (bare && !typing && e.Key == Key.D && img) { Vm?.EnterDevelop(); e.Handled = true; }
        else if (bare && !typing && e.Key == Key.F && img) { ResetZoom(); e.Handled = true; }
        else if (bare && !typing && e.Key == Key.K && img) { ToggleCompare(); e.Handled = true; }
        else if (bare && !typing && e.Key == Key.N && img) { ToggleNegative(); e.Handled = true; }
        // Not while typing: Ctrl+C in a text field still has to copy the text.
        else if (ctrl && !typing && !shift && e.Key == Key.C && img && Vm?.IsLibraryMode == false)
        { OnCopyActiveClick(this, e); e.Handled = true; }
        else if (ctrl && !typing && !shift && e.Key == Key.V && img && Vm?.IsLibraryMode == false)
        { OnPasteActiveClick(this, e); e.Handled = true; }
        else if (ctrl && e.Key == Key.D1 && img) { OnActualSizeClick(this, e); e.Handled = true; }
        else if (ctrl && e.Key == Key.Z && !shift) { Vm?.Undo(); e.Handled = true; }
        else if (ctrl && (e.Key == Key.Y || (e.Key == Key.Z && shift))) { Vm?.Redo(); e.Handled = true; }
        else if (ctrl && e.Key == Key.N) { OnOpenClick(this, e); e.Handled = true; }
        else if (ctrl && e.Key == Key.O && img) { OnAddImagesClick(this, e); e.Handled = true; }
        else if (ctrl && e.Key == Key.E && img) { OnExportClick(this, e); e.Handled = true; }
        else if (ctrl && shift && e.Key == Key.T) { OnToggleThemeClick(this, e); e.Handled = true; }
        else if (ctrl && e.Key == Key.OemComma) { OnPreferencesClick(this, e); e.Handled = true; }
        base.OnKeyDown(e);
    }

    // ── Copy / paste of a frame's parameters ────────────────────────────────────
    //
    // Ctrl+C / Ctrl+V follow the OPEN panel: with 整卷校准 showing they carry Stage-1 calibration,
    // with 帧编辑 showing they carry Stage-2 scene adjustments. Which set you are looking at is
    // what you mean by "copy this" — having one pair of keys for both beats two more chords to
    // remember. The context menus name both explicitly, for when the intent is not the open tab.

    private bool CalibrationTabOpen => PanelTabs.SelectedIndex == 0;

    private void OnCopyActiveClick(object? sender, RoutedEventArgs e)
    {
        if (CalibrationTabOpen) Vm?.CopyCalibration(); else Vm?.CopyScene();
    }

    private void OnPasteActiveClick(object? sender, RoutedEventArgs e)
    {
        if (CalibrationTabOpen) Vm?.PasteCalibrationToCurrent(); else Vm?.PasteSceneToCurrent();
    }

    private void OnPasteCalToCurrentClick(object? sender, RoutedEventArgs e) =>
        Vm?.PasteCalibrationToCurrent();

    private void OnPasteSceneToCurrentClick(object? sender, RoutedEventArgs e) =>
        Vm?.PasteSceneToCurrent();

    private void OnUndoClick(object? sender, RoutedEventArgs e) => Vm?.Undo();
    private void OnRedoClick(object? sender, RoutedEventArgs e) => Vm?.Redo();

    // ── Sampling button handlers ────────────────────────────────────────────────
    private void OnSampleFilmBaseClick(object? sender, RoutedEventArgs e) =>
        ToggleSampling(sender, SampleMode.FilmBase,
            "采样片基：预览已切到负片。对准【最亮的橙色片基】（边缘/帧间未曝光处）拖框，松开即采样。按 Esc 取消。",
            useNegative: true);

    private void OnSampleShadowClick(object? sender, RoutedEventArgs e) =>
        ToggleSampling(sender, SampleMode.WbOffset,
            "采样暗部 WB（wb_offset）：在正片的暗部拖框，松开即采样。按 Esc 取消。",
            useNegative: false);

    private void OnSampleHighlightWbClick(object? sender, RoutedEventArgs e) =>
        ToggleSampling(sender, SampleMode.WbHigh,
            "采样亮部 WB（wb_high）：在正片的【高光中性区】（应为白/灰的最亮处）拖框，松开即采样。按 Esc 取消。",
            useNegative: false);

    private void OnSampleDMaxClick(object? sender, RoutedEventArgs e) =>
        ToggleSampling(sender, SampleMode.DMax,
            "采样 D_max：预览已切到负片。对准负片【最暗处】（=场景高光）拖框，松开即采样。按 Esc 取消。",
            useNegative: true);

    private void OnSampleScanEvClick(object? sender, RoutedEventArgs e) =>
        ToggleSampling(sender, SampleMode.ScanEv,
            "采样偏移：预览已切到负片。框选一块应为纯片基的区域，松开后自动校正零点。按 Esc 取消。",
            useNegative: true);

    private void OnGreyPointClick(object? sender, RoutedEventArgs e) =>
        ToggleSampling(sender, SampleMode.GreyPoint,
            "灰点白平衡：在正片中应为【中性灰/白】的区域拖框，松开即把该区域中和为灰。按 Esc 取消。",
            useNegative: false);

    private void OnSampleBlackClick(object? sender, RoutedEventArgs e) =>
        ToggleSampling(sender, SampleMode.Black,
            "采样黑场：在正片【最暗有效区】拖框，松开即把该处设为黑场端点。按 Esc 取消。",
            useNegative: false);

    private void OnSampleWhiteClick(object? sender, RoutedEventArgs e) =>
        ToggleSampling(sender, SampleMode.White,
            "采样白场：在正片【最亮有效区】拖框，松开即把该处设为白场端点。按 Esc 取消。",
            useNegative: false);

    private void OnCropClick(object? sender, RoutedEventArgs e) =>
        ToggleSampling(sender, SampleMode.Crop, "裁切：拖动框内移动位置，拖角/拖边改变大小（选了预设则锁定比例）。回车应用，Esc 取消。",
            useNegative: false);

    private void OnStraightenHClick(object? sender, RoutedEventArgs e) =>
        ToggleSampling(sender, SampleMode.StraightenH,
            "取水平：沿画面中【应当水平】的边（地平线、水面、桌沿）拖一条线，松开即转正。按 Esc 取消。",
            useNegative: false);

    private void OnStraightenVClick(object? sender, RoutedEventArgs e) =>
        ToggleSampling(sender, SampleMode.StraightenV,
            "取垂直：沿画面中【应当垂直】的边（门框、旗杆、墙角）拖一条线，松开即转正。按 Esc 取消。",
            useNegative: false);

    private void OnAutoLevelsClick(object? sender, RoutedEventArgs e) => Vm?.AutoLevels();
    private void OnAutoDMaxClick(object? sender, RoutedEventArgs e) => Vm?.AutoDetectDMax();
    private void OnAutoWbHighClick(object? sender, RoutedEventArgs e) => Vm?.AutoWbHigh();
    private async void OnAutoWbAiClick(object? sender, RoutedEventArgs e) { if (Vm != null) await Vm.AutoWbAiAsync(); }
    private void OnApplyCalToRollClick(object? sender, RoutedEventArgs e) => Vm?.ApplyCalibrationToRoll();
    private void OnApplySceneToRollClick(object? sender, RoutedEventArgs e) => Vm?.ApplySceneToRoll();
    private void OnCopyCalClick(object? sender, RoutedEventArgs e) => Vm?.CopyCalibration();
    private void OnPasteCalClick(object? sender, RoutedEventArgs e) => Vm?.PasteCalibrationToSelected();
    private void OnCopySceneClick(object? sender, RoutedEventArgs e) => Vm?.CopyScene();
    private void OnPasteSceneClick(object? sender, RoutedEventArgs e) => Vm?.PasteSceneToSelected();

    private async void OnSyncOptionsClick(object? sender, RoutedEventArgs e)
    {
        if (Vm is null) return;
        await new SyncDialog(Vm.Sync).ShowDialog(this);
    }

    private async void OnRollImported()
    {
        if (Vm?.PreviewForDialog is not { } preview) return;
        var dlg = new SprocketDialog(preview);
        bool accepted = await dlg.ShowDialog<bool>(this);
        if (accepted) Vm.ApplySprocketFromDialog(dlg.ResultEnabled, dlg.ResultThreshold);
    }

    private void OnViewNegToggle(object? sender, RoutedEventArgs e)
    {
        if (sender is ToggleButton { IsChecked: true }) Vm?.ShowNegativeView();
        else Vm?.ShowPositiveView();
    }

    // ── View state, driven from three places (button / menu / key) ──────────────
    // The two preview ToggleButtons stay the single source of truth for these states —
    // menu items and shortcuts flip the button so its lit/unlit face never desyncs.
    private void ToggleNegative()
    {
        ViewNegBtn.IsChecked = !(ViewNegBtn.IsChecked ?? false);
        OnViewNegToggle(ViewNegBtn, new RoutedEventArgs());
    }

    private void ToggleCompare()
    {
        CompareBtn.IsChecked = !(CompareBtn.IsChecked ?? false);
        OnCompareToggle(CompareBtn, new RoutedEventArgs());
    }

    private void OnMenuViewNegClick(object? sender, RoutedEventArgs e) { if (Vm?.HasImage == true) ToggleNegative(); }
    private void OnMenuCompareClick(object? sender, RoutedEventArgs e) { if (Vm?.HasImage == true) ToggleCompare(); }

    private void OnQuitClick(object? sender, RoutedEventArgs e) => Close();

    private void OnToggleThemeClick(object? sender, RoutedEventArgs e)
    {
        Services.Settings.Model s = Services.Settings.Current;
        s.Theme = s.Theme == "light" ? "dark" : "light";
        Services.Settings.Save();
        App.ApplyTheme(s.Theme);
    }

    // ── Photo backdrop (预览区右键 / 视图 → 背景色) ──────────────────────────────
    private void OnViewerBgClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string hex }) return;
        Services.Settings.Current.ViewerBackground = hex;
        Services.Settings.Save();
        App.ApplyViewerBackground(hex);
        SyncViewerBgChecks();
    }

    /// <summary>Tick the swatch that matches the saved backdrop, in both copies of the menu
    /// (视图 菜单 and the preview right-click share the item list but not the instances).</summary>
    private void SyncViewerBgChecks()
    {
        string cur = Services.Settings.Current.ViewerBackground;
        foreach (MenuItem parent in new[] { BgMenu, BgMenu2 })
            foreach (object? child in parent.Items)
                if (child is MenuItem { Tag: string hex } mi)
                    mi.IsChecked = string.Equals(hex, cur, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Select the thumbnail under a right-click before its context menu opens.</summary>
    private void OnFilmStripPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(FilmStrip).Properties.IsRightButtonPressed) return;
        if (e.Source is not Visual v) return;
        ListBoxItem? item = v.GetSelfAndVisualAncestors().OfType<ListBoxItem>().FirstOrDefault();
        if (item?.DataContext is Models.RollFrame frame) FilmStrip.SelectedItem = frame;
    }

    private async void OnHelpClick(object? sender, RoutedEventArgs e) => await InfoDialog.Help().ShowDialog(this);
    private async void OnAboutClick(object? sender, RoutedEventArgs e) => await InfoDialog.About().ShowDialog(this);

    // ── Geometry button handlers ────────────────────────────────────────────────
    // Orientation buttons. The ViewModel turns the STORED crop with the frame; these also turn
    // the in-progress draft and the locked ratio, so an edit in flight survives a 90° turn
    // instead of snapping back to the pre-turn shape.
    private void OnRotateCwClick(object? sender, RoutedEventArgs e)
    {
        Vm?.RotateCw();
        if (_cropDraft is { } d) _cropDraft = MainViewModel.RotateCropCw(d);
        InvertCropAspect();
        RenderCropFrame();
    }

    private void OnRotateCcwClick(object? sender, RoutedEventArgs e)
    {
        Vm?.RotateCcw();
        if (_cropDraft is { } d) _cropDraft = MainViewModel.RotateCropCcw(d);
        InvertCropAspect();
        RenderCropFrame();
    }

    private void OnFlipHClick(object? sender, RoutedEventArgs e)
    {
        Vm?.FlipHorizontal();
        if (_cropDraft is { } d) _cropDraft = MainViewModel.FlipCropH(d);
        RenderCropFrame();
    }

    private void OnFlipVClick(object? sender, RoutedEventArgs e)
    {
        Vm?.FlipVertical();
        if (_cropDraft is { } d) _cropDraft = MainViewModel.FlipCropV(d);
        RenderCropFrame();
    }

    /// <summary>A quarter turn puts a 4:3 format on its side, so the lock becomes 3:4. Without
    /// this the next handle drag would snap the just-turned frame back to landscape.</summary>
    private void InvertCropAspect()
    {
        if (_cropAspect is double a && a > 0) _cropAspect = 1.0 / a;
    }
    private void OnClearCropClick(object? sender, RoutedEventArgs e)
    {
        Vm?.ClearCrop();
        // Reset the preset too. Leaving it showing "6×6" with no crop applied means the next
        // free-hand drag is still locked to 1:1 for a ratio the user can no longer see.
        // Index 0 is 自由, whose handler only clears _cropAspect — it applies no crop.
        CropPresetCombo.SelectedIndex = 0;
    }

    /// <summary>Pick an aspect preset: lock the crop drag ratio and drop a centred crop of that ratio.</summary>
    private void OnCropPresetChanged(object? sender, SelectionChangedEventArgs e)
    {
        // Fires during XAML init before named fields exist — read the index off the sender.
        if (sender is not ComboBox combo) return;
        int idx = combo.SelectedIndex;
        if (idx < 0 || idx >= CropAspects.Length) return;
        _cropAspect = CropAspects[idx];
        if (Vm?.HasImage != true) return;
        if (_cropAspect is null)
        {
            // 自由: keep whatever frame is on screen, just stop locking the ratio.
            if (_mode == SampleMode.Crop) RenderCropFrame();
            return;
        }
        // Picking a format ARMS the crop tool and puts a frame up — it no longer commits a crop
        // outright. The user then drags it over the picture and confirms.
        if (_mode != SampleMode.Crop)
        {
            ToggleButton? btn = ToggleFor(SampleMode.Crop);
            SetTogglesExcept(btn);
            if (btn is not null) btn.IsChecked = true;
            EnterMode(SampleMode.Crop, "裁切：拖动框内移动位置，拖角/拖边改变大小（选了预设则锁定比例）。回车应用，Esc 取消。", useNegative: false);
        }
        BeginCropDraft();   // re-seed at the new ratio
    }

    // ── Rubber-band drag on the overlay ─────────────────────────────────────────
    private void OnOverlayPressed(object? sender, PointerPressedEventArgs e)
    {
        var props = e.GetCurrentPoint(Overlay).Properties;
        // Pan on middle drag from any mode, and on LEFT drag while browsing — left-drag is what
        // people actually reach for, and with no sampling tool armed it had no other job.
        // Right-drag used to pan too; that is now the preview's context menu (适合窗口 / 负片 /
        // 裁切 / 背景色 …) and left-drag already covers the gesture.
        bool panGesture = props.IsMiddleButtonPressed
                          || (props.IsLeftButtonPressed && _mode == SampleMode.None);
        if (panGesture)
        {
            if (CanPan())
            {
                _panning = true;
                _panLast = e.GetPosition(ViewPort);
                e.Pointer.Capture(Overlay);
                UpdatePanCursor();
            }
            return;
        }
        if (_mode == SampleMode.None || Vm is null || PreviewImg.Source is null) return;
        _dragStart = e.GetPosition(Overlay);

        if (_mode == SampleMode.Crop)
        {
            // Grab a handle, the interior, or start a fresh rectangle. No rubber band here —
            // the frame itself is the thing being edited and it survives the release.
            if (NormFromOverlay(_dragStart) is not { } n) return;
            _cropHandle = CropHitHandle(_dragStart) ?? "new";
            _cropDragStartNorm = n;
            _cropDragStartRect = _cropDraft ?? (n.X, n.Y, 0, 0);
            if (_cropHandle == "new") _cropDraft = (n.X, n.Y, 0, 0);
            _dragging = true;
            e.Pointer.Capture(Overlay);
            return;
        }

        _dragging = true;
        if (IsLineMode(_mode))
        {
            SelLine.StartPoint = _dragStart;
            SelLine.EndPoint = _dragStart;
            SelLine.IsVisible = true;
        }
        else
        {
            Canvas.SetLeft(SelRect, _dragStart.X);
            Canvas.SetTop(SelRect, _dragStart.Y);
            SelRect.Width = 0;
            SelRect.Height = 0;
            SelRect.IsVisible = true;
        }
        e.Pointer.Capture(Overlay);
    }

    private void OnOverlayMoved(object? sender, PointerEventArgs e)
    {
        if (_panning)
        {
            Point cur = e.GetPosition(ViewPort);
            _pan = new Point(_pan.X + (cur.X - _panLast.X), _pan.Y + (cur.Y - _panLast.Y));
            _panLast = cur;
            ApplyTransform();
            return;
        }
        // Crop mode: hovering shows which handle is under the cursor; dragging edits the frame.
        if (_mode == SampleMode.Crop)
        {
            Point pc = e.GetPosition(Overlay);
            if (!_dragging || _cropHandle is null)
            {
                Overlay.Cursor = new Cursor(CursorForHandle(CropHitHandle(pc)));
                return;
            }
            if (NormFromOverlay(pc) is { } nm) { ApplyCropDrag(nm); RenderCropFrame(); }
            return;
        }

        if (!_dragging) return;
        Point p = e.GetPosition(Overlay);
        if (IsLineMode(_mode)) { SelLine.EndPoint = p; return; }
        Canvas.SetLeft(SelRect, Math.Min(p.X, _dragStart.X));
        Canvas.SetTop(SelRect, Math.Min(p.Y, _dragStart.Y));
        SelRect.Width = Math.Abs(p.X - _dragStart.X);
        SelRect.Height = Math.Abs(p.Y - _dragStart.Y);
    }

    private void OnOverlayReleased(object? sender, PointerReleasedEventArgs e)
    {
        // Panning moved the visible region, so the patch no longer covers what is on screen —
        // re-ask on RELEASE, not per move: a pan is hundreds of events and each one would
        // cancel and restart a render that never finishes.
        if (_panning)
        {
            _panning = false; e.Pointer.Capture(null); UpdatePanCursor();
            SyncSharpPatch();
            return;
        }
        if (!_dragging) return;
        _dragging = false;
        e.Pointer.Capture(null);
        SelRect.IsVisible = false;
        SelLine.IsVisible = false;

        Point end = e.GetPosition(Overlay);
        SampleMode mode = _mode;

        if (mode == SampleMode.Crop)
        {
            // A drag that produced nothing (a stray click on the background) leaves no frame,
            // rather than a degenerate sliver that cannot be grabbed again.
            if (_cropHandle == "new" && _cropDraft is { } d && (d.W < 0.01 || d.H < 0.01))
                _cropDraft = null;
            _cropHandle = null;
            RenderCropFrame();
            return;
        }

        if (IsLineMode(mode))
        {
            ExitMode();
            double? deg = StraightenAngle(_dragStart, end, vertical: mode == SampleMode.StraightenV);
            if (deg is double d && Vm is not null) Vm.ApplyStraightenAngle(d);
            else if (Vm is not null) Vm.StatusText = "拉线太短，未取到角度——请沿参考边拉长一些。";
            return;
        }

        var rect = ToNormalisedRect(_dragStart, end);
        ExitMode();
        if (rect is null || Vm is null) return;

        // Backstop. This runs inside Avalonia's pointer dispatch: anything that escapes here
        // unwinds past the message loop and kills the process, taking every unsaved edit in the
        // roll with it. The samplers guard themselves too; this is the guarantee that no future
        // one can regress that. A rejected sample is a status line, never a lost session.
        try
        {
            switch (mode)
            {
                case SampleMode.FilmBase: Vm.SampleFilmBase(rect.Value); break;
                case SampleMode.WbOffset: Vm.SampleWbOffset(rect.Value); break;
                case SampleMode.WbHigh: Vm.SampleWbHigh(rect.Value); break;
                case SampleMode.DMax: Vm.SampleDMax(rect.Value); break;
                case SampleMode.ScanEv: Vm.SampleScanEv(rect.Value); break;
                case SampleMode.GreyPoint: Vm.SampleGreyPoint(rect.Value); break;
                case SampleMode.Black: Vm.SampleBlack(rect.Value); break;
                case SampleMode.White: Vm.SampleWhite(rect.Value); break;
            }
        }
        catch (Exception ex) { Vm.StatusText = "采样失败：" + ex.Message; }
    }

    /// <summary>
    /// The rotation (clockwise degrees) that lands the drawn line on the horizontal or vertical
    /// axis. Port of preview_widget.py::_straighten_angle.
    ///
    /// The line's screen angle is θ = atan2(dy, dx); screen y points DOWN, so a positive θ is
    /// clockwise — the same sense <see cref="OpenRevelare.Core.Geometry.ApplyRotation"/> uses, so the
    /// result drops straight into 拉直 with no sign juggling. Rotating the image by φ adds φ to the
    /// line's angle, so landing on a target axis needs φ = target − θ.
    ///
    /// Horizontal target is 0°, but θ is first folded into (−90, 90] so a line drawn right-to-left
    /// is the same line as left-to-right. Vertical picks whichever of ±90° is nearer, then folds
    /// the same way — both keep the correction the SHORT way round, so dragging along a doorframe
    /// never asks for a 178° spin.
    ///
    /// No conversion from screen to image space is needed: the preview is letterboxed with a
    /// uniform scale and zoomed with another, and neither changes an angle.
    /// </summary>
    /// <returns>Degrees, or null when the drag was too short to have a meaningful direction.</returns>
    private static double? StraightenAngle(Point a, Point b, bool vertical)
    {
        double dx = b.X - a.X, dy = b.Y - a.Y;
        if (Math.Abs(dx) < 2 && Math.Abs(dy) < 2) return null;

        double theta = Math.Atan2(dy, dx) * 180.0 / Math.PI;
        double phi;
        if (!vertical)
        {
            if (theta > 90.0) theta -= 180.0;
            else if (theta <= -90.0) theta += 180.0;
            phi = -theta;
        }
        else
        {
            double target = Math.Abs(theta - 90.0) <= Math.Abs(theta + 90.0) ? 90.0 : -90.0;
            phi = target - theta;
            if (phi > 90.0) phi -= 180.0;
            else if (phi <= -90.0) phi += 180.0;
        }
        return Math.Clamp(phi, -45.0, 45.0);
    }

    /// <summary>Map two overlay points to a normalised (x,y,w,h) rect in image space,
    /// undoing the Uniform letterbox (the bitmap is centred and scaled to fit).</summary>
    private (double X, double Y, double W, double H)? ToNormalisedRect(Point a, Point b)
    {
        if (PreviewImg.Source is not Bitmap bmp) return null;
        double iw = bmp.PixelSize.Width, ih = bmp.PixelSize.Height;
        double cw = Overlay.Bounds.Width, ch = Overlay.Bounds.Height;
        if (iw <= 0 || ih <= 0 || cw <= 0 || ch <= 0) return null;

        double scale = Math.Min(cw / iw, ch / ih);
        double dispW = iw * scale, dispH = ih * scale;
        double offX = (cw - dispW) / 2, offY = (ch - dispH) / 2;

        double x0 = (Math.Min(a.X, b.X) - offX) / dispW;
        double y0 = (Math.Min(a.Y, b.Y) - offY) / dispH;
        double w = Math.Abs(b.X - a.X) / dispW;
        double h = Math.Abs(b.Y - a.Y) / dispH;

        double x1 = Math.Clamp(x0 + w, 0, 1), y1 = Math.Clamp(y0 + h, 0, 1);
        x0 = Math.Clamp(x0, 0, 1); y0 = Math.Clamp(y0, 0, 1);
        w = x1 - x0; h = y1 - y0;
        if (w < 0.005 || h < 0.005) return null;
        return (x0, y0, w, h);
    }

    // ── Import / export (file pickers need the window's StorageProvider) ─────────
    private async void OnOpenClick(object? sender, RoutedEventArgs e) => await ImportNewRollAsync();

    /// <summary>The single 新建卷 path — 文件 菜单, Ctrl+N, and the 图库's leading tile all land
    /// here. Ends in 修片, because importing a roll is a request to start working on it.</summary>
    private async Task ImportNewRollAsync()
    {
        if (Vm is null) return;
        var dlg = new ImportDialog();
        bool ok = await dlg.ShowDialog<bool>(this);
        if (!ok || dlg.Result is not { } cfg) return;

        // Importing a folder that already has a roll is nearly always a re-open, not a second
        // roll — and making a second one silently would strand the first roll's adjustments.
        if (cfg.Paths.Count > 0
            && System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(cfg.Paths[0])) is { } dir
            && Services.Catalog.InFolder(dir).FirstOrDefault() is { } existing
            && !existing.Missing)
        {
            bool makeNew = false;
            await new InfoDialog("这个文件夹已经有一卷了",
                    $"「{existing.Title}」（{existing.FrameCount} 帧）的工程就在这个文件夹里。\n\n"
                    + "打开它可以接着上次的调整继续；仍然新建会得到一卷全新的、参数从头开始的卷，"
                    + "两者互不影响。")
                .WithAction("仍然新建", "打开已有的卷", () => makeNew = true)
                .ShowDialog(this);
            if (!makeNew)
            {
                await Vm.OpenRollAsync(existing);
                Vm.EnterDevelop();
                return;
            }
        }

        await Vm.LoadRollWithConfigAsync(cfg);
        Vm.EnterDevelop();
    }

    // ── Module switch: 图库 ↔ 修片 ──────────────────────────────────────────────

    private async void OnLibraryModeClick(object? sender, RoutedEventArgs e)
    {
        if (Vm is not null) await Vm.EnterLibraryAsync();
    }

    private void OnDevelopModeClick(object? sender, RoutedEventArgs e) => Vm?.EnterDevelop();

    private async void OnToggleModuleClick(object? sender, RoutedEventArgs e)
    {
        if (Vm is null) return;
        if (Vm.IsLibraryMode) Vm.EnterDevelop();
        else await Vm.EnterLibraryAsync();
    }

    /// <summary>Open a roll picked on the wall, then switch to 修片 — the reason you clicked it.</summary>
    private async Task OnLibraryOpenRequested(ViewModels.RollCard card)
    {
        if (Vm is null || card.Roll is not { } roll) return;
        await Vm.OpenRollAsync(roll);
        Vm.EnterDevelop();
    }

    // ── Catalog: 重定位 / 扫盘 / 最近的卷 ───────────────────────────────────────

    /// <summary>A roll's negatives are missing. Offer to point it at their new folder.</summary>
    private async Task<string?> AskRelinkFolderAsync(int missing, string firstName)
    {
        bool go = false;
        await new InfoDialog("底片不在原来的位置",
                $"这一卷有 {missing} 个源文件找不到了，例如「{firstName}」。\n\n"
                + "如果只是整体移动了文件夹，选中新位置即可按文件名重新对上；"
                + "调整不会丢失。也可以先跳过，稍后再处理。")
            .WithAction("选择文件夹…", "跳过", () => go = true)
            .ShowDialog(this);
        if (!go) return null;

        var dirs = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "底片现在在哪个文件夹", AllowMultiple = false,
        });
        return dirs.FirstOrDefault()?.TryGetLocalPath();
    }

    /// <summary>Rebuild / extend the catalog from .ncproj files on disk.</summary>
    private async void OnScanFolderClick(object? sender, RoutedEventArgs e)
    {
        var dirs = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "扫描此文件夹下的工程（含子文件夹）", AllowMultiple = false,
        });
        if (dirs.FirstOrDefault()?.TryGetLocalPath() is not { } root) return;

        int added = await Task.Run(() => Services.Catalog.Scan(root));
        if (Vm is not null && Vm.IsLibraryMode) await Vm.Library.RefreshAsync();
        await new InfoDialog("扫描完成",
                added > 0 ? $"已加入 {added} 卷。" : "没有找到尚未登记的工程文件（.ncproj）。")
            .ShowDialog(this);
    }

    // ── Catalog: 最近的卷 ───────────────────────────────────────────────────────

    /// <summary>
    /// Rebuild the 最近的卷 submenu each time the 文件 menu opens. Populated from the PARENT
    /// menu's open rather than its own: an empty submenu never opens, so a list built on its own
    /// SubmenuOpened would stay empty forever.
    /// </summary>
    private void OnFileMenuOpened(object? sender, RoutedEventArgs e)
    {
        var recent = Services.Catalog.Recent(10);
        RecentRollsMenu.Items.Clear();
        RecentRollsMenu.IsEnabled = recent.Count > 0;

        foreach (Services.Catalog.Roll roll in recent)
        {
            var item = new MenuItem
            {
                Header = roll.Missing ? roll.Title + "（文件缺失）" : roll.Title,
                IsEnabled = !roll.Missing,
            };
            ToolTip.SetTip(item, $"{roll.Subtitle}\n{roll.FrameCount} 帧 · {roll.ProjectPath}".TrimStart());
            item.Click += async (_, _) =>
            {
                if (Vm is null) return;
                await Vm.OpenRollAsync(roll);
                Vm.EnterDevelop();   // picking a roll means wanting to work on it
            };
            RecentRollsMenu.Items.Add(item);
        }
    }

    // ── Project open / save (.ncproj) ───────────────────────────────────────────
    private static readonly FilePickerFileType NcProjType =
        new("OpenRevelare 工程 (.ncproj)") { Patterns = new[] { "*.ncproj" } };

    private async void OnOpenProjectClick(object? sender, RoutedEventArgs e)
    {
        if (Vm is null) return;
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "打开工程", AllowMultiple = false,
            FileTypeFilter = new[] { NcProjType },
        });
        string? path = files.FirstOrDefault()?.TryGetLocalPath();
        if (path is null) return;
        await Vm.OpenProjectAsync(path);
        Vm.EnterDevelop();
    }

    private async void OnSaveProjectClick(object? sender, RoutedEventArgs e)
    {
        if (Vm is null) return;
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "另存工程副本", DefaultExtension = "ncproj",
            SuggestedFileName = Vm.CurrentRoll?.Title ?? "project",
            FileTypeChoices = new[] { NcProjType },
        });
        string? path = file?.TryGetLocalPath();
        if (path != null) await Vm.SaveProjectAsync(path);
    }

    // ── Roll management (add / virtual copy / remove) ───────────────────────────
    private async void OnAddImagesClick(object? sender, RoutedEventArgs e)
    {
        if (Vm is null) return;
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "添加图像到当前卷",
            AllowMultiple = true,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("负片 (RAW / TIFF)") { Patterns = ImageIo.OpenPatterns },
            },
        });
        var paths = files.Select(f => f.TryGetLocalPath()).Where(p => p != null).Cast<string>().ToList();
        if (paths.Count > 0) await Vm.AddImagesAsync(paths);
    }

    private void OnVirtualCopyClick(object? sender, RoutedEventArgs e) => Vm?.CreateVirtualCopyOfCurrent();
    private void OnRemoveFrameClick(object? sender, RoutedEventArgs e) => Vm?.RemoveCurrentFrame();

    private async void OnExportClick(object? sender, RoutedEventArgs e)
    {
        if (Vm is null) return;
        // Options first, destination second: the format decides the extension the save dialog
        // should be offering, so asking for a filename first asks in the wrong order.
        var opts = new ExportDialog(rollMode: false);
        if (await opts.ShowDialog<bool>(this) != true) return;
        Models.ExportOptions opt = opts.Options;

        bool jpeg = opt.Format == Models.ExportFormat.Jpeg;
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "导出正片",
            DefaultExtension = opt.Extension,
            FileTypeChoices = new List<FilePickerFileType>
            {
                jpeg
                    ? new FilePickerFileType("JPEG") { Patterns = new[] { "*.jpg", "*.jpeg" } }
                    : new FilePickerFileType("16-bit TIFF") { Patterns = new[] { "*.tiff", "*.tif" } },
            },
        });
        string? path = file?.TryGetLocalPath();
        if (path != null) await Vm.ExportAsync(path, opt);
    }

    private async void OnLoadLccClick(object? sender, RoutedEventArgs e)
    {
        if (Vm is null) return;
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "选择 LCC 平场参考图（RAW / TIFF）",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("平场图 (RAW / TIFF)") { Patterns = ImageIo.OpenPatterns },
            },
        });
        string? path = files.FirstOrDefault()?.TryGetLocalPath();
        if (path != null) await Vm.LoadLccAsync(path);
    }

    private async void OnExportRollClick(object? sender, RoutedEventArgs e)
    {
        if (Vm is null) return;
        var opts = new ExportDialog(rollMode: true);
        if (await opts.ShowDialog<bool>(this) != true) return;
        Models.ExportOptions opt = opts.Options;

        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = $"选择整卷导出目录（{opt.Summary()}）",
            AllowMultiple = false,
        });
        string? dir = folders.FirstOrDefault()?.TryGetLocalPath();
        if (dir != null) await Vm.ExportRollAsync(dir, opt);
    }

    private async void OnContactSheetClick(object? sender, RoutedEventArgs e)
    {
        if (Vm is null) return;

        // Build → show → only then ask where to save. Building costs a pass over the whole roll,
        // so the filename prompt has no business coming first.
        if (await Vm.BuildContactThumbsAsync() is not { } thumbs) return;
        var dlg = new ContactSheetDialog(thumbs, Vm.Notes);
        SheetStyle styleBefore = dlg.Style;
        bool confirmed = await dlg.ShowDialog<bool>(this) == true;
        // The dialog is also where the printed look and the roll's notes get changed, and the
        // catalog cover is drawn from both — so it has to be redrawn whether or not an export
        // followed. (Notes already dirty the roll on their own; the style does not.)
        if (dlg.Style != styleBefore) Vm.OnSheetStyleChanged();
        if (!confirmed) return;

        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "导出印样",
            DefaultExtension = "jpg",
            SuggestedFileName = "contactsheet",
            FileTypeChoices = new List<FilePickerFileType>
            {
                new("JPEG") { Patterns = new[] { "*.jpg", "*.jpeg" } },
                new("16-bit TIFF") { Patterns = new[] { "*.tiff", "*.tif" } },
            },
        });
        string? path = file?.TryGetLocalPath();
        if (path != null)
            await Vm.ExportContactSheetAsync(thumbs, dlg.Style, path);
    }

    private void OnResetClick(object? sender, RoutedEventArgs e)
    {
        Curves.ResetAll();
        Vm?.ResetAdjustments();
    }

    private async void OnPreferencesClick(object? sender, RoutedEventArgs e)
        => await new PreferencesDialog().ShowDialog(this);

    private async void OnDocsClick(object? sender, RoutedEventArgs e)
        => await new DocDialog().ShowDialog(this);

    private static string AppVersion => Services.AppInfo.Version;

    /// <summary>
    /// Background update check, 3 s after the window appears — the same shape as the Python
    /// build's <c>QTimer.singleShot(3000, _run_update_check)</c>: delayed so it never slows
    /// startup, and silent unless the manifest advertises something newer.
    /// </summary>
    private async void StartBackgroundUpdateCheck()
    {
        await Task.Delay(3000);
        Services.Updater.UpdateInfo? info = await Services.Updater.CheckAsync(AppVersion);
        if (info is null) return;
        // A modal dialog needs a window that is still there — the user may have quit in the
        // meantime, or be mid-export in another modal.
        if (!IsVisible) return;
        await ShowUpdateDialogAsync(info);
    }

    private async void OnCheckUpdateClick(object? sender, RoutedEventArgs e)
    {
        if (Vm is not null) Vm.StatusText = "正在检查更新 …";
        // Manual check uses Python's longer 8 s timeout: the user is watching, so waiting is
        // preferable to a false "已是最新版本".
        Services.Updater.UpdateInfo? info = await Services.Updater.CheckAsync(AppVersion, 8);
        if (Vm is not null) Vm.StatusText = "";
        if (info is null)
        {
            await new InfoDialog("检查更新", $"当前已是最新版本（{AppVersion}）。").ShowDialog(this);
            return;
        }
        await ShowUpdateDialogAsync(info);
    }

    /// <summary>The 发现新版本 notice — 前往下载 opens the manifest's URL, 稍后再说 dismisses.</summary>
    private async Task ShowUpdateDialogAsync(Services.Updater.UpdateInfo info)
    {
        string changelog = StripHtml(info.Changelog);
        string body = $"OpenRevelare {info.Version} 已发布" +
            (string.IsNullOrEmpty(info.ReleaseDate) ? "" : $"（{info.ReleaseDate}）") +
            $"，当前 {AppVersion}。\n\n" +
            (string.IsNullOrEmpty(changelog) ? "" : $"更新说明：\n{changelog}\n\n") +
            (string.IsNullOrEmpty(info.DownloadUrl) ? "" : $"下载地址：\n{info.DownloadUrl}\n\n") +
            // 三个平台的「怎么装」完全不同，别只写 Windows 的。链接本身也是按平台挑的，
            // 见 Updater.PlatformDownloadUrl。
            (OperatingSystem.IsMacOS()
                ? "下载后打开 dmg，把 OpenRevelare 拖进「应用程序」覆盖旧版即可。"
                  + "首次打开若提示「已损坏」，在终端执行 "
                  + "xattr -dr com.apple.quarantine /Applications/OpenRevelare.app。"
                : OperatingSystem.IsLinux()
                ? "下载后给新的 AppImage 加上可执行权限（chmod +x）替换旧文件即可。"
                : "安装包会直接覆盖当前版本，无需先卸载。")
            + "偏好设置与卷目录都保留在原处。";
        var dlg = new InfoDialog("发现新版本", body);
        if (!string.IsNullOrEmpty(info.DownloadUrl))
            dlg.WithAction("前往下载", "稍后再说", () => Services.Updater.OpenUrl(info.DownloadUrl));
        await dlg.ShowDialog(this);
    }

    /// <summary>Minimal HTML→text for the manifest changelog (it uses &lt;b&gt;/&lt;br&gt;).</summary>
    private static string StripHtml(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        s = System.Text.RegularExpressions.Regex.Replace(s, @"<br\s*/?>", "\n",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        s = System.Text.RegularExpressions.Regex.Replace(s, "<[^>]+>", "");
        return s.Trim();
    }
}
