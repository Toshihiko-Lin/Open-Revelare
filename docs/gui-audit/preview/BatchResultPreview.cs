using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using OpenRevelare.Gui;
using OpenRevelare.Gui.Services;
using OpenRevelare.Gui.Views;

internal static class BatchResultPreview
{
    internal static void Run(string output)
    {
        foreach (string language in new[] { "zh", "en" })
        foreach (bool dark in new[] { false, true })
        foreach (int width in new[] { 480, 780 })
        {
            Loc.Apply(language);
            App.Current!.RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light;
            var items = Enumerable.Range(1, 200).Select(i => new BatchExportItem($"D:/samples/frame-{i:000}.raf", $"D:/output/frame-{i:000}.tif", _ => Task.FromResult(""))
            { State = i % 4 == 0 ? BatchExportState.Failed : BatchExportState.Completed }).ToArray();
            items[3].Error = "Cannot decode frame. " + new string('x', 300);
            var job = new BatchExportJob("D:/output", items, "Example roll", "TIFF 16-bit · sRGB") { RecoveryBlockedReason = "The project's calibration reference changed; create a new export task." };
            var dialog = new BatchExportReportDialog(job, path => path == items[3].SourcePath) { Width = width, Height = 520 };
            dialog.Show(); Dispatcher.UIThread.RunJobs();
            T Find<T>(string name) where T : Control => dialog.GetVisualDescendants().OfType<T>().Single(c => c.Name == name);
            var filter = Find<ComboBox>("ResultFilter");
            var rows = Find<ListBox>("ResultRows");
            var detail = Find<TextBox>("ResultDetails");
            var retry = Find<Button>("RetryResults");
            var locate = Find<Button>("LocateSource");
            if (retry.IsEnabled || rows.ItemCount != 200 || detail.Text?.Contains(items[3].Error) != true || !locate.IsEnabled)
                throw new Exception("Initial ordering, detail, locate or blocked recovery guard failed");
            filter.SelectedIndex = 2; Dispatcher.UIThread.RunJobs();
            if (rows.ItemCount != 50) throw new Exception("Failed filter count incorrect");
            filter.SelectedIndex = 3; Dispatcher.UIThread.RunJobs();
            if (rows.ItemCount != 0 || locate.IsEnabled) throw new Exception("Empty filter retained a stale selection action");
            var copy = Find<Button>("CopyFullReport");
            var copyPoint = copy.TranslatePoint(new Point(copy.Bounds.Width / 2, copy.Bounds.Height / 2), dialog)!.Value;
            dialog.MouseDown(copyPoint, MouseButton.Left); dialog.MouseUp(copyPoint, MouseButton.Left);
            Dispatcher.UIThread.RunJobs();
            var clipboard = dialog.Clipboard!.GetTextAsync();
            var copyWatch = System.Diagnostics.Stopwatch.StartNew();
            while (!clipboard.IsCompleted)
            {
                Dispatcher.UIThread.RunJobs(); Thread.Sleep(1);
                if (copyWatch.ElapsedMilliseconds > 10000) throw new Exception("Clipboard read timed out");
            }
            string copied = clipboard.GetAwaiter().GetResult() ?? "";
            if (!copied.Contains(items[199].SourcePath) || !copied.Contains(items[3].Error))
                throw new Exception("Full report copy lost filtered-out rows or error details");
            filter.SelectedIndex = 0; Dispatcher.UIThread.RunJobs();
            var point = retry.TranslatePoint(default, dialog)!.Value;
            if (point.Y + retry.Bounds.Height > dialog.ClientSize.Height + 1 || rows.Bounds.Height <= 0)
                throw new Exception("Small-window actions or result list clipped");
            using var bitmap = new RenderTargetBitmap(new PixelSize(width, 520));
            bitmap.Render(dialog);
            var name = $"batch-{language}-{(dark ? "dark" : "light")}-{width}";
            bitmap.Save(Path.Combine(output, name + ".png"));
            dialog.Close();
            Console.WriteLine(name + ": 200 results, state filters, empty selection, details, locate and recovery guard passed");
        }
    }
}
