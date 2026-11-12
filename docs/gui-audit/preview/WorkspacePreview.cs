using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using OpenRevelare.Core;
using OpenRevelare.Gui;
using OpenRevelare.Gui.Models;
using OpenRevelare.Gui.Services;
using OpenRevelare.Gui.ViewModels;
using OpenRevelare.Gui.Views;

internal static class WorkspacePreview
{
    internal static void Run(string output)
    {
        const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance;
        foreach (string language in new[] { "zh", "en" })
        foreach (bool dark in new[] { false, true })
        {
            Loc.Apply(language);
            App.Current!.RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light;
            using var vm = new MainViewModel();
            var frame = new RollFrame("sample.tif") { Params = new FrameParams { ExposureEv = 0.3, CropRect = (0.1, 0.1, 0.8, 0.8), SplitCell = (0, 0, 0.5, 1) } };
            vm.Frames.Add(frame);
            vm.GetType().GetField("_currentFrame", flags)!.SetValue(vm, frame);
            vm.GetType().GetField("_paramsLoaded", flags)!.SetValue(vm, true);
            vm.GetType().GetMethod("LoadParams", flags)!.Invoke(vm, [frame.Params]);
            vm.HasImage = true; vm.EnterDevelop();
            using var sample = new Bitmap(Path.GetFullPath("docs/gui-concept/stageshow/src/frame.jpg"));
            vm.PreviewImage = sample;
            var window = new MainWindow { DataContext = vm };
            window.Show();
            foreach (var (width, height) in new[] { (800, 520), (1000, 700), (1280, 800), (1440, 900), (800, 700) })
            {
                window.Width = width; window.Height = height;
                window.UpdateLayout();
                AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                Dispatcher.UIThread.RunJobs();
                var preview = window.FindControl<Border>("PreviewViewport")!;
                var inspector = window.FindControl<Grid>("InspectorWorkspace")!;
                var rail = window.FindControl<Border>("FilmRail")!;
                var toggle = window.FindControl<ToggleButton>("CompactFilmRailButton")!;
                var pages = window.FindControl<TabControl>("PanelTabs")!;
                void Inside(Control control)
                {
                    var pos = control.TranslatePoint(default, window)!.Value;
                    if (pos.X < -1 || pos.Y < -1 || pos.X + control.Bounds.Width > window.ClientSize.Width + 1
                        || pos.Y + control.Bounds.Height > window.ClientSize.Height + 1 || control.Bounds.Height < 1)
                        throw new Exception($"{control.Name} outside workspace {width}x{height}: {pos}, {control.Bounds}, client={window.ClientSize}");
                }
                Inside(preview); Inside(inspector);
                if (height < 650 && window.FindControl<Expander>("AnalysisExpander")!.IsExpanded)
                    throw new Exception("Short workspace did not collapse auxiliary analysis");
                if (window.FindControl<ScrollViewer>("PanelScroll")!.Bounds.Height < 90)
                    throw new Exception("Parameter scroll area is too small");
                Inside(window.FindControl<Grid>("NavigationActions")!);
                if (preview.Bounds.Width < 240 || preview.Bounds.Height < 140) throw new Exception("Preview became unusably small");
                if (Grid.GetRow(inspector) != 0) throw new Exception("Inspector moved below image");
                pages.SelectedIndex = 1; Dispatcher.UIThread.RunJobs();
                Inside(window.FindControl<Button>("SceneSyncFooter")!);
                if (width == 800)
                {
                    if (rail.IsVisible || !toggle.IsVisible) throw new Exception($"Narrow workspace did not collapse film rail: window={window.Bounds}, rail={rail.IsVisible}, toggle={toggle.IsVisible}");
                    Click(toggle); Dispatcher.UIThread.RunJobs();
                    if (!rail.IsVisible) throw new Exception("Film rail cannot be opened");
                    Inside(rail); Inside(preview); Inside(inspector);
                    Click(toggle); Dispatcher.UIThread.RunJobs();
                    if (rail.IsVisible) throw new Exception("Film rail cannot be closed");
                }
                else if (!rail.IsVisible) throw new Exception("Wide workspace lost film rail");
                if (!ReferenceEquals(vm.CurrentFrame, frame) || vm.ExposureEv != 0.3 || frame.Params.SplitCell != (0, 0, 0.5, 1))
                    throw new Exception("Layout changed frame or editing data");
                using var bitmap = new RenderTargetBitmap(new PixelSize(width, height));
                bitmap.Render(window);
                string name = $"workspace-{language}-{(dark ? "dark" : "light")}-{width}x{height}";
                bitmap.Save(Path.Combine(output, name + ".png"));
                if (width == 800 && height == 700)
                {
                    var dust = window.FindControl<ToggleButton>("DustToolBtn")!;
                    Click(dust); Inside(preview);
                    if (!window.FindControl<StackPanel>("DustToolbar")!.IsVisible) throw new Exception("Dust tools inaccessible");
                    Click(dust);
                    window.GetType().GetMethod("OnCropClick", flags)!.Invoke(window, [null, new Avalonia.Interactivity.RoutedEventArgs()]);
                    window.UpdateLayout(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); Dispatcher.UIThread.RunJobs();
                    var cancel = window.FindControl<Button>("CropCancelBtn")!;
                    if (!cancel.IsEffectivelyVisible) throw new Exception("Crop transaction actions missing");
                    Inside(cancel); Inside(window.FindControl<Button>("CropApplyBtn")!); Inside(preview);
                    Click(cancel);
                    if (vm.CropEditing) throw new Exception("Crop cancellation did not restore editing state");
                }
                Console.WriteLine(name + ": preview/inspector bounds, rail mouse toggle, footer and resize preservation passed");
            }
            window.DataContext = null;
            window.Close();
            Dispatcher.UIThread.RunJobs();
            void Click(Control control)
            {
                var point = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;
                window.MouseDown(point, MouseButton.Left); window.MouseUp(point, MouseButton.Left);
                window.UpdateLayout(); AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                Dispatcher.UIThread.RunJobs();
            }
        }
    }
}
