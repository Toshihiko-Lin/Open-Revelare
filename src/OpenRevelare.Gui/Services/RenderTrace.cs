using System.Diagnostics;

namespace OpenRevelare.Gui.Services;

/// <summary>
/// Opt-in timing trace for the interactive preview path. Off unless the process starts with
/// <c>OPENREVELARE_RENDER_TRACE=1</c>; then each stage appends one line to
/// <c>%TEMP%\openrevelare-render-trace.log</c>.
///
/// It exists because "the slider does not follow the pointer" cannot be diagnosed from a
/// benchmark: the cost that matters is the one paid on THIS roll, at THIS viewport, on THIS
/// display contract, and it is split between the UI thread (the drag render) and the
/// presentation worker (compose + pack + present). Reading both from one log is what tells which
/// half to work on.
/// </summary>
internal static class RenderTrace
{
    public static readonly bool Enabled =
        Environment.GetEnvironmentVariable("OPENREVELARE_RENDER_TRACE") == "1";

    private static readonly string LogPath =
        Path.Combine(Path.GetTempPath(), "openrevelare-render-trace.log");

    private static readonly object Gate = new();

    public static Stopwatch? Start() => Enabled ? Stopwatch.StartNew() : null;

    public static void Write(string line)
    {
        if (!Enabled) return;
        lock (Gate)
        {
            try { File.AppendAllText(LogPath, $"{DateTime.Now:HH:mm:ss.fff} {line}{Environment.NewLine}"); }
            catch (IOException) { }
        }
    }
}
