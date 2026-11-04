using System.Diagnostics;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using OpenRevelare.Core;
using OpenRevelare.Gui.Services;
using OpenRevelare.Gui.ViewModels;

internal static class Program
{
    internal static readonly Stopwatch Clock = new();
    internal static string Output = "";

    [STAThread]
    public static void Main(string[] args)
    {
        // Input must be a disposable staging directory: the real application writes its
        // project beside the source images. Hard links preserve source bytes without copies.
        string input = Path.GetFullPath(args[0]);
        Output = Path.GetFullPath(args[1]);
        Directory.CreateDirectory(Path.GetDirectoryName(Output)!);
        if (File.Exists(Output)) throw new IOException("Choose a new output file for each run.");
        string[] paths = Directory.GetFiles(input).Where(RawDecode.IsRawExtension).Order(StringComparer.Ordinal).ToArray();
        string state = Path.Combine(Path.GetDirectoryName(Output)!, Path.GetFileNameWithoutExtension(Output) + "-state");
        Environment.SetEnvironmentVariable("OPENREVELARE_CONFIG_DIR", Path.Combine(state, "config"));
        Environment.SetEnvironmentVariable("OPENREVELARE_DATA_DIR", Path.Combine(state, "data"));
        Environment.SetEnvironmentVariable("OPENREVELARE_RENDER_TRACE", "1");
        Settings.Current.DecodeConcurrency = int.Parse(args.ElementAtOrDefault(2) ?? "0");
        Settings.Current.AutoInvertOnImport = true;
        Settings.Current.Language = "zh";
        Settings.Current.GpuMode = GpuAccelerationMode.Auto;
        RawDecode.ClearSourceCache();
        Clock.Start();
        AppBuilder.Configure<ImportBenchmarkApp>().UsePlatformDetect().WithInterFont()
            .StartWithClassicDesktopLifetime(paths);
    }

    internal static void Write(object value)
    {
        string line = JsonSerializer.Serialize(value);
        Console.WriteLine(line);
        File.AppendAllText(Output, line + Environment.NewLine);
    }
}

public sealed class ImportBenchmarkApp : OpenRevelare.Gui.App
{
    public override void OnFrameworkInitializationCompleted()
    {
        base.OnFrameworkInitializationCompleted();
        if (ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop
            || desktop.MainWindow?.DataContext is not MainViewModel vm) return;
        bool seenFrames = false, seenPreview = false, seenAnalysis = false;
        int stable = 0;
        string last = "";
        Program.Write(new { Kind = "start", NativeBuild = RawDecode.NativeBuildInfo,
            Workers = ImageIo.PreviewWorkers, Free = ImageIo.AutoConcurrencyInfo().FreeBytes });
        DispatcherTimer.Run(() =>
        {
            if (vm.Frames.Count > 0) seenFrames = true;
            if (vm.PreviewImage is not null && !seenPreview)
            {
                seenPreview = true;
                Program.Write(new { Kind = "first-preview", ElapsedMs = Program.Clock.Elapsed.TotalMilliseconds });
            }
            seenAnalysis |= vm.RollAnalysisProgress.Length > 0 || vm.RollAnalysisPending;
            string state = $"{vm.StatusText}|{vm.RollAnalysisProgress}|{vm.WarmupProgress}|{vm.BackgroundStatus}|thumbs={vm.Frames.Count(f => f.Thumbnail is not null)}";
            if (state != last)
            {
                Program.Write(new { Kind = "state", ElapsedMs = Program.Clock.Elapsed.TotalMilliseconds, State = state });
                last = state;
            }
            bool complete = seenFrames && seenPreview && seenAnalysis && !vm.IsBusy && !vm.LoadingImportedPreview
                && !vm.RollAnalysisPending && vm.RollAnalysisProgress.Length == 0 && vm.WarmupProgress.Length == 0
                && vm.BackgroundStatus.Length == 0 && vm.Frames.All(f => f.Thumbnail is not null);
            stable = complete ? stable + 1 : 0;
            if (stable >= 4 || Program.Clock.Elapsed.TotalMinutes > 12)
            {
                using var process = Process.GetCurrentProcess();
                Program.Write(new { Kind = "complete", Success = stable >= 4, ElapsedMs = Program.Clock.Elapsed.TotalMilliseconds,
                    Files = vm.Frames.Count, Thumbnails = vm.Frames.Count(f => f.Thumbnail is not null),
                    PeakWorkingSetBytes = process.PeakWorkingSet64, ProcessId = Environment.ProcessId });
                typeof(MainViewModel).Assembly.GetType("OpenRevelare.Gui.Services.RenderTrace")!
                    .GetMethod("SaveSummary")!.Invoke(null, null);
                string summary = Path.Combine(Path.GetTempPath(), $"openrevelare-performance-{Environment.ProcessId}.json");
                if (File.Exists(summary)) File.Copy(summary, Program.Output + ".performance.json", overwrite: true);
                desktop.MainWindow.Close();
                return false;
            }
            return true;
        }, TimeSpan.FromMilliseconds(250));
    }
}
