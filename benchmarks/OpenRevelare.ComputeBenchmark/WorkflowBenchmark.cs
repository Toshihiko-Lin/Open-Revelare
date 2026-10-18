using System.Diagnostics;
using System.Text.Json;
using OpenRevelare.ColorManagement;
using OpenRevelare.Core;
using OpenRevelare.Presentation;

internal static class WorkflowBenchmark
{
    internal static void Run(string? output)
    {
        string root = Path.Combine(Path.GetTempPath(), "revelare-gpu-workflow-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var results = new List<object>();
        var previous = GpuAcceleration.Mode;
        try
        {
            using var cmm = new LittleCmsEngine();
            foreach (var (width, height) in new[] { (1600, 1000), (4000, 2000) })
            {
                var fixture = new ImageBuffer(width, height);
                var random = new Random(42);
                for (int i = 0; i < fixture.Data.Length; i++) fixture.Data[i] = 0.02f + random.NextSingle() * 0.8f;
                string input = Path.Combine(root, "input.tif"), export = Path.Combine(root, "export.jpg");
                TiffIO.ExportTiff16(fixture, input, TiffIO.CompressionMode.None);
                foreach (string? stock in new string?[] { null, ":kodak-2383" })
                {
                    float[]? reference = null;
                    float[]? referenceDelivery = null;
                    foreach (var mode in new[] { GpuAccelerationMode.Off, GpuAccelerationMode.On, GpuAccelerationMode.Auto })
                    {
                        GpuAcceleration.ReleaseResources();
                        GpuAcceleration.Mode = mode;
                        var phases = new Dictionary<string, List<double>>();
                        var parameters = new FrameParams { PrintLut = stock ?? "", ExposureEv = 0.35, Contrast = 0.12, Saturation = 0.08 };
                        double cold = 0;
                        for (int run = -1; run < 5; run++)
                        {
                            long total = Stopwatch.GetTimestamp();
                            T Time<T>(string phase, Func<T> operation)
                            {
                                long start = Stopwatch.GetTimestamp();
                                T value = operation();
                                if (run >= 0)
                                {
                                    if (!phases.TryGetValue(phase, out var times)) phases[phase] = times = [];
                                    times.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
                                }
                                return value;
                            }
                            var source = Time("import.tiff", () => TiffIO.LoadWorkingFrame(input,
                                TiffInputAssumption.Linear, ColorPipelineVersion.ManagedV2, cmm));
                            var rendered = Time("render", () => Pipeline.Render(source, parameters, ColorPipelineVersion.ManagedV2, cmm));
                            if (reference is null) reference = rendered.Pixels.Data;
                            if (!GpuAcceleration.Equivalent(reference, rendered.Pixels.Data))
                            {
                                int worst = Enumerable.Range(0, reference.Length).MaxBy(i => Math.Abs(reference[i] - rendered.Pixels.Data[i]));
                                throw new Exception($"Workflow render parity failed: index={worst}, CPU={reference[worst]}, GPU={rendered.Pixels.Data[worst]}, {GpuAcceleration.Diagnostics}");
                            }
                            Time("preview.scene", () => CanonicalPreviewConverter.Convert(rendered, cmm, 1));
                            var delivery = Time("export.prepare", () => rendered.WithPixels(OutputSharpen.ResizeAndApply(
                                rendered.Pixels, width / 2, false, OutputSharpen.Level.Standard)));
                            referenceDelivery ??= delivery.Pixels.Data;
                            if (!GpuAcceleration.Equivalent(referenceDelivery,delivery.Pixels.Data)) throw new Exception("Delivery parity failed");
                            Time("export.jpeg", () => { JpegIO.ExportJpeg(delivery, export, 90); return true; });
                            double elapsed = Stopwatch.GetElapsedTime(total).TotalMilliseconds;
                            if (run < 0) cold = elapsed;
                            else
                            {
                                if (!phases.TryGetValue("total", out var times)) phases["total"] = times = [];
                                times.Add(elapsed);
                            }
                        }
                        var result = new { Width = width, Height = height, Stock = stock ?? "analytic", Mode = mode.ToString(),
                            Runs = 5, Parity = true, FirstRunMs = cold,
                            MedianMs = phases.ToDictionary(p => p.Key, p => p.Value.Order().ElementAt(2)),
                            Decisions = GpuAcceleration.Measurements(), Diagnostics = GpuAcceleration.Diagnostics };
                        results.Add(result);
                        Console.WriteLine(JsonSerializer.Serialize(result));
                    }
                }
            }
            if (output is not null)
            {
                string path = Path.GetFullPath(output);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, JsonSerializer.Serialize(new { Schema = "openrevelare-gpu-workflow-v2",
                    TimestampUtc = DateTime.UtcNow, Scope = "Generated uncompressed TIFF -> ManagedV2 render -> canonical preview -> half-size standard-sharpened JPEG. Warm OS cache unspecified; no RAW, UI compositor or display latency. Total includes parity comparison. v1 had no delivery preparation and is not a comparable total.",
                    Results = results }, new JsonSerializerOptions { WriteIndented = true }));
            }
        }
        finally
        {
            GpuAcceleration.ReleaseResources();
            GpuAcceleration.Mode = previous;
            foreach (string path in Directory.EnumerateFiles(root)) File.Delete(path);
            Directory.Delete(root);
        }
    }
}
