using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.Media.Imaging;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;
using System.Reflection;
using OpenRevelare.Gui;
using OpenRevelare.Gui.Models;
using OpenRevelare.Gui.Services;
using OpenRevelare.Gui.Views;
using OpenRevelare.Gui.ViewModels;
using OpenRevelare.Core;
using SkiaSharp;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        string output = Path.GetFullPath(args.FirstOrDefault() ?? "docs/gui-audit/renders");
        Directory.CreateDirectory(output);
        string isolated = Path.Combine(Path.GetTempPath(), "OpenRevelare-gui-preview", Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("OPENREVELARE_CONFIG_DIR", Path.Combine(isolated, "config"));
        Environment.SetEnvironmentVariable("OPENREVELARE_DATA_DIR", Path.Combine(isolated, "data"));
        AppBuilder.Configure<App>().UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .SetupWithoutStarting();
        if (args.Contains("--batch-results"))
        {
            BatchResultPreview.Run(output);
            return;
        }
        if (args.Contains("--workspace"))
        {
            WorkspacePreview.Run(output);
            return;
        }
        Loc.Apply("zh");
        string photoPath = Path.GetFullPath("docs/gui-concept/stageshow/src/frame.jpg");
        using var sample = SKBitmap.Decode(photoPath);
        var samplePixels = sample.Pixels;
        var sampleImage = new ImageBuffer(sample.Width, sample.Height);
        for (int i = 0; i < samplePixels.Length; i++)
        {
            sampleImage.Data[i * 3] = samplePixels[i].Red / 255f;
            sampleImage.Data[i * 3 + 1] = samplePixels[i].Green / 255f;
            sampleImage.Data[i * 3 + 2] = samplePixels[i].Blue / 255f;
        }
        foreach (bool dark in new[] { false, true })
        {
            App.Current!.RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light;
            string theme = dark ? "dark" : "light";
            void Render(string name, Window window)
            {
                App.Current!.RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light;
                window.Show();
                if (name.StartsWith("inspector") || name == "library") window.Classes.Remove("dialog");
                Dispatcher.UIThread.RunJobs();
                if (name == "inspector-calibration")
                {
                    var strip = window.GetVisualDescendants().OfType<TabStrip>().Single(t => t.Name == "InspectorTabs");
                    var pages = window.GetVisualDescendants().OfType<TabControl>().Single(t => t.Name == "PanelTabs");
                    if (!strip.ContainerFromIndex(0)!.Focus(NavigationMethod.Tab))
                        throw new Exception("Inspector tab could not receive keyboard focus");
                    window.KeyPress(Key.Right, RawInputModifiers.None, PhysicalKey.ArrowRight, null);
                    window.KeyRelease(Key.Right, RawInputModifiers.None, PhysicalKey.ArrowRight, null);
                    Dispatcher.UIThread.RunJobs();
                    if (strip.SelectedIndex != 1 || pages.SelectedIndex != 1 || pages.SelectedContent is null)
                        throw new Exception($"Inspector arrow navigation failed: strip={strip.SelectedIndex}, page={pages.SelectedIndex}, content={pages.SelectedContent?.GetType().Name}");
                    window.KeyPress(Key.Left, RawInputModifiers.None, PhysicalKey.ArrowLeft, null);
                    window.KeyRelease(Key.Left, RawInputModifiers.None, PhysicalKey.ArrowLeft, null);
                    Dispatcher.UIThread.RunJobs();
                    if (strip.SelectedIndex != 0 || pages.SelectedIndex != 0)
                        throw new Exception("Inspector arrow-key navigation did not return to calibration");
                    var histogram = window.GetVisualDescendants().OfType<OpenRevelare.Gui.Controls.HistogramView>().Single();
                    var sidebar = window.GetVisualDescendants().OfType<Grid>().Single(g => g.Name == "InspectorWorkspace");
                    if (histogram.Background.ToString() != sidebar.Background?.ToString())
                        throw new Exception($"Histogram/background mismatch: {histogram.Background} / {sidebar.Background}");
                    Console.WriteLine($"inspector-{theme}: arrow navigation, content binding and histogram surface passed");
                }
                Avalonia.Headless.AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                using var frame = window.CaptureRenderedFrame() ?? throw new Exception($"No pixels: {name}");
                frame.Save(Path.Combine(output, $"{name}-{theme}.png"));
                Console.WriteLine($"{name}-{theme}: {frame.PixelSize}");
                window.Close();
                Dispatcher.UIThread.RunJobs();
            }
            Render("import", new ImportDialog(new[] { "示例底片_001.tif", "示例底片_002.tif", "示例底片_003.tif" }, false));
            Render("export", new ExportDialog());
            Render("preferences", new PreferencesDialog());
            Render("sync", new SyncDialog(new SyncOptions()));
            Render("roll-info", new RollInfoDialog("示例卷 · 2026", new RollNotes { FilmStock = "Color Negative", FilmIso = "400", DevProcess = "C-41" }));
            Render("rename", new PromptDialog("重命名卷", "卷名", "示例卷 · 2026"));
            Render("notice", new InfoDialog("导出完成", "6 张已导出。\n输出色彩空间：Display P3"));
            Render("shortcuts", InfoDialog.Help());
            Render("report", new FrameReportDialog("OpenRevelare · 示例技术报告\n\n输入\n  TIFF · 16-bit\n\n标定\n  D_min  0.198  0.429  0.651\n\n此数据仅用于版式检查。"));
            var plans = StripPlan.Detect("示例扫描.tif", sampleImage);
            using var stripPreview = new Bitmap(photoPath);
            foreach (var plan in plans) plan.Preview = stripPreview;
            Render("split", new SplitDialog(plans));
            Render("contact-sheet", new ContactSheetDialog(Enumerable.Repeat(sampleImage, 6).ToArray(),
                new RollNotes { FilmStock = "示例胶卷", FilmIso = "400", DevProcess = "C-41", CameraBody = "示例相机" }));
            Render("docs", new DocDialog());
            Render("save-failure", new SaveFailureDialog("示例：目标目录不可写。\n请检查目录权限，或选择可写的位置另存恢复副本。"));
            Render("dependencies", new ProjectDependenciesDialog("示例卷", new[]
            {
                new ProjectDependencies.Entry("D:/Photos/示例卷/001.tif", false, new[] { ProjectDependencies.Kind.Original }, true, new[] { 1 }, ProjectDependencies.Availability.Present, 12000000),
                new ProjectDependencies.Entry("D:/Calibration/flat.tif", false, new[] { ProjectDependencies.Kind.Lcc }, true, Array.Empty<int>(), ProjectDependencies.Availability.Missing, null),
            }));
            using var vm = new MainViewModel();
            typeof(MainViewModel).GetField("_reordering", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(vm, true);
            for (int i = 1; i <= 6; i++) vm.Frames.Add(new RollFrame($"示例底片_{i:000}.tif"));
            vm.CurrentFrame = vm.Frames[2];
            vm.HasImage = true;
            vm.Histogram = OpenRevelare.Gui.Controls.HistogramData.FromBuffer(sampleImage.Data);
            vm.Waveform = OpenRevelare.Gui.Controls.WaveformData.FromBuffer(sampleImage);
            vm.EnterDevelop();
            var main = new MainWindow { DataContext = vm };
            var inspector = main.FindControl<Grid>("InspectorWorkspace")!;
            ((Grid)inspector.Parent!).Children.Remove(inspector);
            inspector.DataContext = vm;
            inspector.Margin = default;
            var inspectorHost = new Window { Content = inspector, Width = 410, Height = 800 };
            Render("inspector-calibration", inspectorHost);
            if (main.FindControl<TabControl>("PanelTabs")!.SelectedContent is null)
                throw new Exception("Pinned inspector tab lost its parameter content");
            // Re-use the actual tab/content tree, not a recreated mock panel.
            inspectorHost.Content = null;
            main.FindControl<TabControl>("PanelTabs")!.SelectedIndex = 1;
            Render("inspector-finishing", new Window { Content = inspector, Width = 410, Height = 800 });
            Render("library", new Window { Content = new LibraryView { DataContext = vm.Library }, Width = 1280, Height = 800 });
            typeof(MainWindow).GetField("_bannerHintDismissed", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(main, true);
            typeof(MainWindow).GetMethod("ApplyBannerVisibility", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(main, new object[] { true });
            if (!main.FindControl<Button>("CropApplyBtn")!.IsVisible || !main.FindControl<Button>("CropCancelBtn")!.IsVisible
                || !main.FindControl<Border>("Banner")!.IsVisible || main.FindControl<TextBlock>("BannerText")!.IsVisible)
                throw new Exception("Dismissing crop explanation hid transaction actions");
            Loc.Apply("en");
            Render("preferences-en-narrow", new PreferencesDialog { Width = 480, Height = 520 });
            Loc.Apply("en");
            Render("export-en-narrow", new ExportDialog { Width = 480, Height = 520, SizeToContent = SizeToContent.Manual });
            Loc.Apply("zh");
        }
        VerifyPreferencesExit();
    }

    private static void VerifyPreferencesExit()
    {
        Settings.Current.Theme = "light"; Settings.Current.Language = "zh";
        foreach (string exit in new[] { "close", "escape", "cancel" })
        {
            App.ApplyTheme(); Loc.Apply();
            var dialog = new PreferencesDialog();
            dialog.Show(); Dispatcher.UIThread.RunJobs();
            ComboBox Field(string name) => (ComboBox)typeof(PreferencesDialog).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(dialog)!;
            Field("_backend").SelectedIndex = 1;
            Field("_theme").SelectedIndex = 0;
            Field("_language").SelectedIndex = 2;
            Dispatcher.UIThread.RunJobs();
            if (Field("_backend").SelectedIndex != 1) throw new Exception("Language rebuild lost draft preferences");
            if (exit == "escape") dialog.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
            else if (exit == "cancel")
                dialog.GetLogicalDescendants().OfType<Button>().Single(b => b.IsCancel)
                    .RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            else dialog.Close();
            Dispatcher.UIThread.RunJobs();
            if (App.Current!.RequestedThemeVariant != ThemeVariant.Light || Loc.Current != "zh")
                throw new Exception($"Preferences preview not rolled back on {exit}");
            Console.WriteLine($"preferences-{exit}: theme/language rollback and language-rebuild draft retention passed");
        }
        var saveDialog = new PreferencesDialog();
        saveDialog.Show(); Dispatcher.UIThread.RunJobs();
        ComboBox SaveField(string name) => (ComboBox)typeof(PreferencesDialog).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(saveDialog)!;
        SaveField("_theme").SelectedIndex = 0;
        SaveField("_language").SelectedIndex = 2;
        saveDialog.GetLogicalDescendants().OfType<Button>().Single(b => b.IsDefault)
            .RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
        if (Settings.Current.Theme != "dark" || Settings.Current.Language != "en"
            || App.Current!.RequestedThemeVariant != ThemeVariant.Dark || Loc.Current != "en")
            throw new Exception("Saving preferences rolled back its committed preview");
        Console.WriteLine("preferences-save: committed theme/language retained after close");
    }
}
