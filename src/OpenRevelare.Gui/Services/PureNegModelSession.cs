using System.Diagnostics;
using System.Text.Json;

namespace OpenRevelare.Gui.Services;

// Keep the GPU graph and weights resident between frames. Rebuilding a DML graph
// for every photograph can cost more time than inference on a small photograph.
internal static class PureNegModelSession
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static Process? _process;
    private static string? _component;

    public static async Task<JsonElement> InferAsync(string component, string source, string output, CancellationToken ct)
    {
        await Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_process is null || _process.HasExited || _component != component)
            {
                Shutdown();
                var start = new ProcessStartInfo(Path.Combine(component, "runtime",
                    OperatingSystem.IsWindows() ? "pureneg-model-host.exe" : "pureneg-model-host"))
                {
                    UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true,
                    RedirectStandardOutput = true, RedirectStandardError = true,
                };
                start.ArgumentList.Add("--server");
                _process = Process.Start(start) ?? throw new IOException("Cannot start PURENeg inference runtime.");
                _component = component;
                // Drain stderr so an ORT diagnostic cannot fill the pipe and stall a frame.
                _process.ErrorDataReceived += (_, _) => { };
                _process.BeginErrorReadLine();
                var initializing = _process;
                using var cancelInit = ct.Register(() => Stop(initializing));
                using var ready = JsonDocument.Parse(await initializing.StandardOutput.ReadLineAsync(ct)
                    ?? throw new IOException("PURENeg inference runtime exited during initialization."));
                if (ready.RootElement.GetProperty("type").GetString() != "ready")
                    throw new IOException("PURENeg inference runtime did not become ready.");
            }
            var process = _process;
            using var cancel = ct.Register(() => Stop(process));
            await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new[] { source, output }).AsMemory(), ct);
            await process.StandardInput.FlushAsync(ct);
            using var response = JsonDocument.Parse(await process.StandardOutput.ReadLineAsync(ct)
                ?? throw new IOException("PURENeg inference runtime exited during prediction."));
            if (response.RootElement.GetProperty("type").GetString() != "model-ready")
                throw new IOException("PURENeg inference runtime returned no probability map.");
            return response.RootElement.Clone();
        }
        catch { Shutdown(); throw; }
        finally { Gate.Release(); }
    }

    public static void Shutdown()
    {
        var process = Interlocked.Exchange(ref _process, null);
        if (process is null) return;
        Stop(process);
        process.Dispose();
        _component = null;
    }

    private static void Stop(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
    }
}
