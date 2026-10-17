using System.Diagnostics;
using System.Text.Json;
using OpenRevelare.Core;

// Generated data only. Includes transfer, allocation, both GPU passes and readback.
// Fails if the real OS GPU backend cannot initialize; no software-device substitution.
GpuAcceleration.Mode = GpuAccelerationMode.Off;
using var session = new GpuComputeSession();
foreach (var (width, height) in new[] { (1600, 1200), (4000, 2000), (6000, 4000) })
{
    var source = new ImageBuffer(width, height);
    var random = new Random(42);
    for (int i = 0; i < source.Data.Length; i++) source.Data[i] = random.NextSingle();
    float[] kernel = new float[7];
    int radius = 3;
    float total = 0;
    for (int k = -radius; k <= radius; k++) { kernel[k + radius] = MathF.Exp(-(k * k) / (2f * 0.7f * 0.7f)); total += kernel[k + radius]; }
    for (int k = 0; k < kernel.Length; k++) kernel[k] /= total;

    var cpuResize = new List<double>(); var gpuResize = new List<double>();
    var cpuSharpen = new List<double>(); var gpuSharpen = new List<double>();
    float[] expectedResize = [], expectedSharpen = [];
    for (int run = -1; run < 5; run++)
    {
        long start = Stopwatch.GetTimestamp();
        var reference = Resample.ToLongEdge(source, width / 2, false);
        double cpuR = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        start = Stopwatch.GetTimestamp();
        float[] actual = session.Resize(source, width / 2, height / 2);
        double gpuR = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        if (!GpuAcceleration.Equivalent(reference.Data, actual)) throw new Exception("Resize parity failed");

        var sharpen = new ImageBuffer(width, height, (float[])source.Data.Clone());
        start = Stopwatch.GetTimestamp();
        OutputSharpen.Apply(sharpen, OutputSharpen.Level.Standard);
        double cpuS = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        var destination = new float[source.Data.Length];
        start = Stopwatch.GetTimestamp();
        float[] gpu = session.Sharpen(source, kernel, 0.65f, 2f / 255f, 0.12f);
        gpu.CopyTo(destination, 0); // Include the production in-place publication copy.
        double gpuS = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        if (!GpuAcceleration.Equivalent(sharpen.Data, destination)) throw new Exception("Sharpen parity failed");
        expectedResize = reference.Data;
        expectedSharpen = sharpen.Data;
        if (run < 0) continue;
        cpuResize.Add(cpuR); gpuResize.Add(gpuR); cpuSharpen.Add(cpuS); gpuSharpen.Add(gpuS);
    }
    double Median(List<double> samples) => samples.Order().ElementAt(samples.Count / 2);
    Console.WriteLine(JsonSerializer.Serialize(new {
        Width = width, Height = height, Runs = 5, Parity = true,
        ResizeCpuMs = Median(cpuResize), ResizeGpuMs = Median(gpuResize),
        SharpenCpuMs = Median(cpuSharpen), SharpenGpuMs = Median(gpuSharpen),
    }));
    if (width == 6000)
    {
        GpuAcceleration.Mode = GpuAccelerationMode.Auto;
        var automaticResize = Resample.ToLongEdge(source, width / 2, false);
        var automaticSharpen = new ImageBuffer(width, height, (float[])source.Data.Clone());
        OutputSharpen.Apply(automaticSharpen, OutputSharpen.Level.Standard);
        if (!GpuAcceleration.Equivalent(expectedResize, automaticResize.Data)
            || !GpuAcceleration.Equivalent(expectedSharpen, automaticSharpen.Data))
            throw new Exception("Auto scheduler parity failed");
        var decisions = GpuAcceleration.Measurements();
        if (decisions.Length != 2) throw new Exception("Auto scheduler did not calibrate both workloads");
        Console.WriteLine(JsonSerializer.Serialize(new { AutoParity = true, Decisions = decisions, Diagnostics = GpuAcceleration.Diagnostics }));
        GpuAcceleration.ReleaseResources();
    }
}
