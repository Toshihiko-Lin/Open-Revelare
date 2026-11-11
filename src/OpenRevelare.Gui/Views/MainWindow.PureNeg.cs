using System.Globalization;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Input;
using Avalonia.Threading;
using OpenRevelare.Core;
using OpenRevelare.Gui.Services;
using OpenRevelare.Gui.ViewModels;

namespace OpenRevelare.Gui.Views;

public partial class MainWindow
{
    private enum DustView { Mask, After }

    private sealed record DustReviewDraft(MainViewModel.PureNegInput Input,
        DustReviewProjection.GeometryKey Geometry,
        DustReviewProjection Projection, DustMaskEditor Editor, bool Detected = true);

    private DustReviewDraft? _dustReviewDraft;
    private CancellationTokenSource? _dustReviewOperation;
    private DustView _dustView = DustView.After;
    private IPointer? _dustComparisonPointer;
    private bool _dustComparisonKeyDown, _dustComparisonButtonKeyDown;
    private Key? _dustComparisonReleaseKey;
    private bool IsDustComparing => _dustComparisonPointer is not null ||
        _dustComparisonKeyDown || _dustComparisonButtonKeyDown;
    private bool HasDustReview => _dustReviewOperation is not null || _dustReviewDraft is not null;

    private void InitializeDustToolbar()
    {
        Closed += (_, _) => ResetDustReview(refresh: false);
        Deactivated += (_, _) =>
        {
            _dustComparisonReleaseKey = null;
            EndDustComparison();
        };
        DustCompareBtn.AddHandler(InputElement.PointerPressedEvent, OnDustComparePressed, RoutingStrategies.Tunnel);
        DustCompareBtn.AddHandler(InputElement.PointerReleasedEvent, OnDustCompareReleased, RoutingStrategies.Tunnel);
        DustCompareBtn.PointerCaptureLost += (_, e) =>
        {
            if (!ReferenceEquals(_dustComparisonPointer, e.Pointer)) return;
            _dustComparisonPointer = null;
            UpdateDustReviewDisplay();
        };
        DustCompareBtn.AddHandler(InputElement.KeyDownEvent, (_, e) =>
        {
            if (e.KeyModifiers != KeyModifiers.None || e.Key is not (Key.Space or Key.Enter or Key.Return)) return;
            if (CanCompareDust && !_dustComparisonButtonKeyDown)
            {
                _dustComparisonButtonKeyDown = true;
                _dustComparisonReleaseKey = e.Key;
                ShowDustComparison();
            }
            e.Handled = true;
        }, RoutingStrategies.Tunnel);
        // Consume the matching release before its new focus target sees it. Otherwise
        // Space-up can click a different tool after focus leaves the comparison button.
        AddHandler(InputElement.KeyUpEvent, (_, e) =>
        {
            if (e.Key == Key.K && _dustComparisonKeyDown) _dustComparisonKeyDown = false;
            else if (e.Key == _dustComparisonReleaseKey)
            {
                _dustComparisonReleaseKey = null;
                _dustComparisonButtonKeyDown = false;
            }
            else return;
            UpdateDustReviewDisplay();
            e.Handled = true;
        }, RoutingStrategies.Tunnel, handledEventsToo: true);
        DustCompareBtn.LostFocus += (_, _) =>
        {
            if (!_dustComparisonButtonKeyDown) return;
            _dustComparisonButtonKeyDown = false;
            UpdateDustReviewDisplay();
        };
    }

    private void OnDustToolClick(object? sender, RoutedEventArgs e)
    {
        if (DustToolBtn.IsChecked != true) { CloseDustToolbar(); return; }
        if (Vm?.HasImage != true) { DustToolBtn.IsChecked = false; return; }
        if (_mode != SampleMode.None) ExitMode();
        EnterDustPositiveView();
        DustToolbar.IsVisible = true;
        UpdateDustReviewDisplay();
    }

    private void EnterDustPositiveView()
    {
        if (ViewNegBtn.IsChecked == true) ToggleNegative();
    }

    private void CloseDustToolbar()
    {
        if (!DustToolbar.IsVisible) return;
        if (IsDustMode) ExitMode();
        ResetDustReview();
        DustOptionsBtn.Flyout?.Hide();
        DustRadiusButton.Flyout?.Hide();
        DustToolBtn.IsChecked = false;
        DustToolbar.IsVisible = false;
        UpdateDustToolbar();
    }

    private void ResetDustReview(bool refresh = true)
    {
        EndDustComparison(refresh: false);
        EndOverlayGesture();
        if (IsDustMode) ExitMode();
        var operation = _dustReviewOperation;
        _dustReviewOperation = null;
        operation?.Cancel();
        _dustReviewDraft = null;
        _dustView = DustView.After;
        DustReviewStatus.Text = null;
        if (Vm is { } vm)
        {
            vm.SetDustReviewPreview(null, refresh);
            vm.ShowDustReviewOverlay(null, null);
            vm.ShowDustMask = false;
            vm.IsDustReviewActive = false;
        }
        UpdateDustToolbar();
    }

    private void UpdateDustToolbar()
    {
        bool reviewing = _dustReviewDraft is not null;
        bool idle = _dustReviewOperation is null;
        bool installed = PureNegComponent.IsInstalled;
        bool open = DustToolbar.IsVisible;
        UpdateNoticeStripPlacement();
        ViewNegBtn.IsVisible = PreviewOrientationTools.IsVisible = !open;
        DustToolRail.IsVisible = open;
        RenderSummaryButton.IsVisible = ExportButton.IsVisible = ExportMoreButton.IsVisible = !open;
        DustAddBtn.IsEnabled = DustEraseBtn.IsEnabled = idle;
        DustUndoBtn.IsEnabled = idle && (reviewing ? _dustReviewDraft!.Editor.CanUndo : Vm?.CanUndo == true);
        DustRedoBtn.IsEnabled = idle && (reviewing ? _dustReviewDraft!.Editor.CanRedo : Vm?.CanRedo == true);
        DustRadiusButton.IsEnabled = idle;
        DustOptionsBtn.IsEnabled = idle;
        DustDetectionOptions.IsEnabled = idle && PureNegComponent.IsSupported;
        DustClearActions.IsVisible = !reviewing;
        DustDetectBtn.IsEnabled = idle && PureNegComponent.IsSupported;
        DustDetectLabel.Text = Loc.T("PURENeg 自动除尘");
        DustDownloadIcon.IsVisible = !installed;
        DustDetectIcon.IsVisible = installed;
        ToolTip.SetTip(DustDetectBtn, !PureNegComponent.IsSupported
            ? Loc.T("当前平台没有可用的自动除尘组件。")
            : !installed ? Loc.T("首次使用会下载可选组件，完成后自动检测并预览修复效果。")
            : reviewing ? Loc.T("重新检测灰尘，完成后预览修复效果。")
            : Loc.T("自动检测灰尘，完成后预览修复效果。"));
        DustViewSelector.IsEnabled = idle;
        DustMaskViewBtn.IsChecked = !IsDustComparing && _dustView == DustView.Mask;
        DustCompareLabel.Text = Loc.T(IsDustComparing ? "除尘前" : "对比");
        DustCompareBtn.Classes.Set("comparing", IsDustComparing);
        DustApplyBtn.IsVisible = open;
        DustApplyBtn.IsEnabled = idle;
        DustApplyBtn.Content = reviewing ? Loc.T("应用修复") : Loc.T("完成");
        DustCancelBtn.IsVisible = open && HasDustReview;
        DustCancelBtn.Content = idle ? Loc.T("取消") : installed ? Loc.T("取消检测") : Loc.T("取消下载");
        ToolTip.SetTip(DustCancelBtn, _dustReviewDraft is { Detected: false }
            ? Loc.T("放弃修改") : Loc.T("放弃本次检测"));
        DustReviewStatus.IsVisible = open;
    }

    private async void OnPureNegReviewClick(object? sender, RoutedEventArgs e)
    {
        if (_dustReviewOperation is not null || !PureNegComponent.IsSupported ||
            Vm is not { CurrentFrame: { } frame, HasLongTask: false, IsBusy: false } vm) return;
        string[] categories = new[] {
            DetectDustKind.IsChecked == true ? "dust" : "",
            DetectFiberKind.IsChecked == true ? "fibers" : "",
            DetectScratchKind.IsChecked == true ? "scratches" : "",
        }.Where(value => value.Length != 0).ToArray();
        if (categories.Length == 0)
        {
            DustReviewStatus.Text = Loc.T("请选择至少一种检测类型。");
            UpdateDustToolbar();
            return;
        }
        string polarity = DustPolarity.SelectedIndex switch { 1 => "bright", 2 => "dark", _ => "auto" };
        string sensitivity = DustSensitivity.Value.ToString(CultureInfo.InvariantCulture);
        DustOptionsBtn.Flyout?.Hide();
        EndDustComparison(refresh: false);
        EndOverlayGesture();
        if (_mode != SampleMode.None) ExitMode();
        EnterDustPositiveView();
        using var owner = new CancellationTokenSource();
        _dustReviewOperation = owner;
        CancellationToken ct = owner.Token;
        DirectoryInfo? workspace = null;
        bool IsCurrent() => ReferenceEquals(_dustReviewOperation, owner) && ReferenceEquals(vm.CurrentFrame, frame);
        void Status(string value)
        {
            if (!IsCurrent()) return;
            DustReviewStatus.Text = value;
            UpdateDustToolbar();
        }
        var elapsed = new Stopwatch();
        PureNegComponent.DetectionProgress detection = new(0, null, null);
        bool detecting = false;
        bool installing = false;
        void DetectionStatus()
        {
            if (!detecting) return;
            string stage = Loc.T(detection.Message ?? "正在检测灰尘");
            Status($"{stage} · {detection.Percent}% · {elapsed.Elapsed:m\\:ss}");
        }
        var clock = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        clock.Tick += (_, _) => DetectionStatus();
        var progress = new Progress<PureNegComponent.DetectionProgress>(value =>
        {
            detection = value;
            DetectionStatus();
        });

        try
        {
            ct.ThrowIfCancellationRequested();
            if (!PureNegComponent.IsInstalled)
            {
                installing = true;
                Status(Loc.T("正在下载 PURENeg 除尘"));
                var downloadProgress = new Progress<int>(value =>
                {
                    if (installing) Status(Loc.F($"正在下载 PURENeg 除尘（{value}%）"));
                });
                await PureNegComponent.InstallAsync(downloadProgress, ct);
                installing = false;
                if (!IsCurrent()) return;
                ct.ThrowIfCancellationRequested();
            }
            workspace = Directory.CreateTempSubdirectory("openrevelare-pureneg-");
            string path = Path.Combine(workspace.FullName, "input.tiff");
            Status(Loc.T("正在渲染全分辨率照片"));
            var input = await vm.WritePureNegInputAsync(path, ct);
            input.Stamp.RequireCurrent();

            Status(Loc.T("正在检测灰尘"));
            detecting = true;
            elapsed.Start();
            clock.Start();
            PureNegComponent.ReviewImage review;
            PureNegComponent.CandidateRun[] runs;
            using (RenderTrace.Scope("dust.detect"))
            {
                review = await PureNegComponent.PrepareAsync(workspace.FullName, path, polarity, sensitivity,
                    string.Join(",", categories), progress, ct);
                RenderTrace.Count(review.Native ? "dust.engine.native" : "dust.engine.javascript");
                if (review.Width != input.Width || review.Height != input.Height)
                    throw new InvalidDataException("PURE Neg changed the source grid.");
                runs = await Task.Run(() => PureNegComponent.ReadRuns(review, ct), ct);
            }
            detecting = false;
            clock.Stop();
            if (!IsCurrent()) return;
            Status(Loc.T("正在准备蒙版审核"));
            input.Stamp.RequireCurrent();
            if (vm.CurrentDustReviewGeometry() is not { } geometry) return;
            var projection = await Task.Run(() =>
                new DustReviewProjection(geometry, input.Width, input.Height, input.Bounds, runs), ct);
            if (!IsCurrent()) return;
            if (vm.CurrentDustReviewGeometry() != geometry)
            {
                Status(Loc.T("画面构图已更改，请重新检测。"));
                return;
            }
            if (projection.VisibleIds.Count == 0)
            {
                ResetDustReview();
                DustReviewStatus.Text = Loc.T("当前画面内未检测到除尘候选。");
                UpdateDustToolbar();
                return;
            }
            var selected = new DustMask(input.Width, input.Height, input.Bounds,
                runs.Where(run => projection.VisibleIds.Contains(run.RegionId)).Select(run => run.Run));
            _dustReviewDraft = new(input, geometry, projection, new(selected));
            _dustView = DustView.After;
            vm.IsDustReviewActive = true;
            UpdateDustReviewDisplay();
            _dustReviewOperation = null;
            UpdateDustToolbar();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (IsCurrent())
            {
                DustReviewStatus.Text = Loc.T(installing ? "PURENeg 除尘下载失败" : "自动除尘失败") + " · " + ex.Message;
                UpdateDustToolbar();
            }
        }
        finally
        {
            installing = false;
            detecting = false;
            clock.Stop();
            if (workspace is not null) PureNegComponent.DeleteTemporaryDirectory(workspace.FullName);
            if (ReferenceEquals(_dustReviewOperation, owner))
            {
                _dustReviewOperation = null;
                UpdateDustToolbar();
            }
        }
    }

    private void UpdateDustReviewDisplay()
    {
        if (Vm is not { } vm) return;
        var draft = _dustReviewDraft;
        bool after = !IsDustComparing && _dustView == DustView.After;
        bool showMask = !IsDustComparing && _dustView == DustView.Mask;
        vm.SetDustReviewPreview(after ? draft?.Editor.Mask : null, before: !after);
        vm.ShowDustMask = showMask && draft is null;
        vm.ShowDustReviewOverlay(showMask ? draft?.Projection : null, draft?.Editor.Mask);
        DustReviewStatus.Text = null;
        if (draft is not null)
        {
            int total = draft.Projection.VisibleIds.Count;
            string summary = draft.Detected ? Loc.F($"{total} 处") : Loc.T("除尘");
            if (draft.Editor.IsModified) summary += " · " + Loc.T("已手动调整");
            DustReviewStatus.Text = summary + " · " + DustOperationHint();
        }
        else DustReviewStatus.Text = DustOperationHint();
        UpdateDustToolbar();
    }

    private string DustOperationHint() => _mode switch
    {
        _ when IsDustComparing => Loc.T("正在显示除尘前，松开预览修复"),
        _ when _dustView == DustView.After && _dustReviewDraft is not null =>
            Loc.T("修复预览，满意后点击应用修复"),
        SampleMode.DustAdd => Loc.T("拖动补画漏检，松开鼠标完成一笔"),
        SampleMode.DustErase => Loc.T("拖动恢复原图，松开鼠标完成一笔"),
        _ when _dustReviewDraft is not null => Loc.T("拖动画面，使用画笔增减除尘范围"),
        _ when Vm?.HasAutomaticDust == true => Loc.T("除尘已应用，可继续微调"),
        _ => Loc.T("拖动画面，滚轮缩放"),
    };

    private void SetDustView(DustView view)
    {
        if (_dustReviewOperation is not null) return;
        EndDustComparison(refresh: false);
        if (view != DustView.Mask && IsDustMode) ExitMode();
        _dustView = view;
        UpdateDustReviewDisplay();
    }

    private void OnDustReviewViewClick(object? sender, RoutedEventArgs e)
    {
        SetDustView(DustMaskViewBtn.IsChecked == true ? DustView.Mask : DustView.After);
    }

    private bool CanCompareDust => DustToolbar.IsVisible && _dustReviewOperation is null && Vm?.HasImage == true;

    private void ShowDustComparison()
    {
        EndOverlayGesture();
        _dustView = DustView.After;
        DustBrushRing.IsVisible = false;
        UpdateDustReviewDisplay();
    }

    private void EndDustComparison(bool refresh = true)
    {
        if (!IsDustComparing) return;
        var pointer = _dustComparisonPointer;
        _dustComparisonPointer = null;
        _dustComparisonKeyDown = _dustComparisonButtonKeyDown = false;
        if (ReferenceEquals(pointer?.Captured, DustCompareBtn)) pointer.Capture(null);
        if (refresh) UpdateDustReviewDisplay();
    }

    private void OnDustComparePressed(object? sender, PointerPressedEventArgs e)
    {
        if (!CanCompareDust || !e.GetCurrentPoint(DustCompareBtn).Properties.IsLeftButtonPressed) return;
        _dustComparisonPointer = e.Pointer;
        e.Pointer.Capture(DustCompareBtn);
        DustCompareBtn.Focus();
        ShowDustComparison();
        e.Handled = true;
    }

    private void OnDustCompareReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (e.InitialPressMouseButton != MouseButton.Left ||
            !ReferenceEquals(_dustComparisonPointer, e.Pointer)) return;
        _dustComparisonPointer = null;
        if (ReferenceEquals(e.Pointer.Captured, DustCompareBtn)) e.Pointer.Capture(null);
        UpdateDustReviewDisplay();
        e.Handled = true;
    }

    private void BeginAppliedDustReview()
    {
        if (_dustReviewDraft is not null || Vm is not { } vm ||
            vm.AppliedDustReview() is not { } applied || vm.CurrentDustReviewGeometry() is not { } geometry) return;
        var runs = applied.Mask.EnumerateRuns().Select(run => new PureNegComponent.CandidateRun(0, run)).ToArray();
        var projection = new DustReviewProjection(geometry, applied.Input.Width, applied.Input.Height,
            applied.Input.Bounds, runs);
        _dustReviewDraft = new(applied.Input, geometry, projection, new(applied.Mask), Detected: false);
        vm.IsDustReviewActive = true;
    }

    private bool EditDustReviewStroke(IEnumerable<Point> samples, bool restore)
    {
        if (_dustReviewOperation is not null || _dustReviewDraft is not { } draft ||
            Vm is not { } vm || LetterboxRect() is not { } box) return false;
        try
        {
            var brushes = samples.Select(point =>
            {
                double radius = vm.DustBrushRadiusOnDisplay(point.X, point.Y, box.Width, box.Height);
                return draft.Projection.MakeBrush(point, radius / box.Width, radius / box.Height);
            }).Where(brush => brush.HasValue).Select(brush => brush!.Value);
            if (!draft.Editor.Paint(brushes, restore)) return false;
            UpdateDustReviewDisplay();
            return true;
        }
        catch (ArgumentException ex)
        {
            DustReviewStatus.Text = Loc.T("蒙版修改未应用") + " · " + ex.Message;
            UpdateDustToolbar();
            return false;
        }
    }

    private void OnUndoDustMaskClick(object? sender, RoutedEventArgs e)
    {
        if (_dustReviewOperation is not null || _dustReviewDraft?.Editor.Undo() != true) return;
        UpdateDustReviewDisplay();
    }

    private void OnRedoDustMaskClick(object? sender, RoutedEventArgs e)
    {
        if (_dustReviewOperation is not null || _dustReviewDraft?.Editor.Redo() != true) return;
        UpdateDustReviewDisplay();
    }

    private void CheckDustReviewGeometry()
    {
        if (_dustReviewDraft is not { } draft || Vm?.CurrentDustReviewGeometry() == draft.Geometry) return;
        ResetDustReview();
        DustReviewStatus.Text = Loc.T("画面构图已更改，请重新检测。");
        UpdateDustToolbar();
    }

    private void OnApplyDustReviewClick(object? sender, RoutedEventArgs e)
    {
        if (_dustReviewDraft is null)
        {
            if (_dustReviewOperation is null) CloseDustToolbar();
            return;
        }
        CheckDustReviewGeometry();
        if (_dustReviewDraft is not { } draft || Vm is not { } vm || !DustApplyBtn.IsEnabled) return;
        try
        {
            if (IsDustMode) ExitMode();
            vm.ApplyPureNegMask(draft.Input, draft.Editor.Mask);
            ResetDustReview(refresh: false);
        }
        catch (Exception ex)
        {
            DustReviewStatus.Text = Loc.T("自动除尘失败") + " · " + ex.Message;
            UpdateDustToolbar();
        }
    }

    private void OnCancelDustReviewClick(object? sender, RoutedEventArgs e)
    {
        if (_dustReviewOperation is not { } operation) { ResetDustReview(); return; }
        _dustReviewOperation = null;
        operation.Cancel();
        DustReviewStatus.Text = null;
        if (_dustReviewDraft is not null) UpdateDustReviewDisplay();
        else UpdateDustToolbar();
    }

    private void OnClearAutomaticDustClick(object? sender, RoutedEventArgs e) => Vm?.ClearAutomaticDust();
}
