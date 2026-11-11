using System.Diagnostics;
using System.Text.Json;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using BitMiracle.LibTiff.Classic;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

// Isolated from the GUI's Deep-WB ORT runtime: CPU/DML/CUDA packages cannot collide.
const int tile = 576, keep = 512, pad = 32, plane = tile * tile;
try
{
    string model = Path.Combine(AppContext.BaseDirectory, "FibreNet.onnx");
    string modelSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(model))).ToLowerInvariant();
    bool probe = args is ["--probe"];
    bool server = args is ["--server"];
    if (!probe && !server && args.Length != 2) throw new ArgumentException("Expected input.tiff output.u8 or --probe.");
    string provider = "cpu";
    string? fallback = null;
    var clock = Stopwatch.StartNew();
    InferenceSession session;
    // The TIFF input is RGB8-normalized. Reuse exact FP16 encodings instead
    // of performing millions of software Float16 conversions per tile.
    var normalized = Enumerable.Range(0, 256).Select(v => (Float16)(v / 255f)).ToArray();
    var quantized = new byte[65536];
    for (int bits = 0; bits < quantized.Length; bits++)
    {
        float value = (float)BitConverter.UInt16BitsToHalf((ushort)bits);
        if (float.IsFinite(value)) quantized[bits] = (byte)Math.Clamp(MathF.Round(value * 255, MidpointRounding.AwayFromZero), 0, 255);
    }
    SessionOptions Options() => new()
    {
        EnableMemoryPattern = false, ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
        GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
    };
    try
    {
        using var options = Options();
#if PURENEG_DML
        provider = "directml";
        options.AppendExecutionProvider_DML(0);
#elif PURENEG_CUDA
        provider = "cuda";
        options.AppendExecutionProvider_CUDA(0);
#endif
        session = new(model, options);
    }
    catch (Exception error) when (provider != "cpu" || error is OnnxRuntimeException)
    {
        fallback = error.GetType().Name;
        provider = "cpu";
        using var options = Options();
        session = new(model, options);
    }
    try
    {
        if (session.InputMetadata["rgb"].Dimensions is not [1, 3, tile, tile] ||
            session.OutputMetadata["prob"].Dimensions is not [1, 1, tile, tile])
            throw new InvalidDataException("Unexpected FibreNet model grid.");
        void RunImage(string[] request, bool isProbe)
        {
        clock.Restart();
        byte[] rgb;
        int width, height;
        if (isProbe) { width = height = 1; rgb = [128, 128, 128]; }
        else
        {
            using var tiff = Tiff.Open(request[0], "r") ?? throw new IOException("Cannot read detector TIFF.");
            width = tiff.GetField(TiffTag.IMAGEWIDTH)[0].ToInt();
            height = tiff.GetField(TiffTag.IMAGELENGTH)[0].ToInt();
            int depth = tiff.GetField(TiffTag.BITSPERSAMPLE)[0].ToInt();
            if (width <= 0 || height <= 0 || (long)width * height > 300_000_000 || depth is not (8 or 16) ||
                tiff.GetField(TiffTag.SAMPLESPERPIXEL)[0].ToInt() != 3 ||
                tiff.GetField(TiffTag.PLANARCONFIG)[0].ToInt() != (int)PlanarConfig.CONTIG ||
                tiff.GetField(TiffTag.PHOTOMETRIC)[0].ToInt() != (int)Photometric.RGB)
                throw new InvalidDataException("Expected the RGB detector snapshot.");
            rgb = new byte[checked(width * height * 3)];
            byte[] row = new byte[checked(width * 3 * depth / 8)];
            for (int y = 0; y < height; y++)
            {
                if (!tiff.ReadScanline(row, y)) throw new IOException("Cannot read detector TIFF row.");
                for (int x = 0; x < width * 3; x++)
                    rgb[y * width * 3 + x] = depth == 8 ? row[x]
                        : (byte)((BitConverter.ToUInt16(row, x * 2) + 128) / 257);
            }
        }
        var map = new byte[checked(width * height)];
        Float16[] input = new Float16[plane * 3];
        var named = NamedOnnxValue.CreateFromTensor("rgb", new DenseTensor<Float16>(input, [1, 3, tile, tile]));
        int tiles = 0;
        for (int ty = 0; ty < height; ty += keep)
        for (int tx = 0; tx < width; tx += keep)
        {
            for (int y = 0; y < tile; y++)
            {
                int sy = Math.Clamp(ty - pad + y, 0, height - 1);
                for (int x = 0; x < tile; x++)
                {
                    int sx = Math.Clamp(tx - pad + x, 0, width - 1), s = (sy * width + sx) * 3, d = y * tile + x;
                    input[d] = normalized[rgb[s]]; input[plane + d] = normalized[rgb[s + 1]]; input[2 * plane + d] = normalized[rgb[s + 2]];
                }
            }
            using var result = session.Run([named]);
            var tensor = result.First(v => v.Name == "prob").AsTensor<Float16>();
            ReadOnlySpan<Float16> values = tensor is DenseTensor<Float16> dense ? dense.Buffer.Span : tensor.ToArray();
            var bits = MemoryMarshal.Cast<Float16, ushort>(values);
            for (int y = 0; y < Math.Min(keep, height - ty); y++)
            for (int x = 0; x < Math.Min(keep, width - tx); x++)
            {
                ushort v = bits[(y + pad) * tile + x + pad];
                if ((v & 0x7c00) == 0x7c00) throw new InvalidDataException("FibreNet returned non-finite probability.");
                map[(ty + y) * width + tx + x] = quantized[v];
            }
            tiles++;
        }
        if (!isProbe) File.WriteAllBytes(request[1], map);
        Console.WriteLine(JsonSerializer.Serialize(new { type = "model-ready", provider, fallback, tiles, width, height,
            modelSha256, precision = "fp16-network-fp32-pool-accumulation", milliseconds = clock.ElapsedMilliseconds }));
        }
        void Predict(string[] request, bool isProbe)
        {
            try { RunImage(request, isProbe); }
            catch (Exception error) when (provider != "cpu" && error is OnnxRuntimeException)
            {
                // Retry the entire image, rather than mixing GPU/CPU tiles in one map.
                session.Dispose();
                provider = "cpu"; fallback = error.GetType().Name;
                using var options = Options();
                session = new(model, options);
                RunImage(request, isProbe);
            }
        }
        if (server)
        {
            Console.WriteLine(JsonSerializer.Serialize(new { type = "ready", provider, fallback }));
            while (Console.ReadLine() is { } line)
            {
                var request = JsonSerializer.Deserialize<string[]>(line);
                if (request is not { Length: 2 }) throw new InvalidDataException("Invalid model request.");
                Predict(request, false);
            }
        }
        else Predict(args, probe);
    }
    finally { session.Dispose(); }
}
catch (Exception error)
{
    Console.Error.WriteLine(error.Message);
    return 1;
}
return 0;
