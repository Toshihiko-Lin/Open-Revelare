using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using OpenRevelare.Core;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace OpenRevelare.DeepWb.Onnx;

/// <summary>
/// Deep White Balance (net_awb.onnx) backend — port of the onnxruntime half of
/// negative/white_balance.py (deep_wb_correct_once / auto_wb_onnx / nn_wb_high_step /
/// _resize_for_inference).
///
/// Lives outside OpenRevelare.Core so Core stays dependency-free; Core only declares
/// <see cref="IDeepWbCorrector"/> and the CLI/GUI injects this. No runtime discovery is
/// needed — Microsoft.ML.OnnxRuntime ships native binaries for Windows, Linux and macOS, so
/// a plain project reference is portable.
///
/// ⚠ NOT bit-comparable with Python. The net's own output depends on the onnxruntime
/// build, and the pre-resize goes through PIL's LANCZOS there vs ImageSharp's here. The
/// verification bar (HANDOFF §7) is that the CONVERGED wb_high/wb_offset land in the same
/// ballpark, not that pixels match. The arithmetic around the net — the affine solve and
/// the iteration loop — lives in Core and IS parity-tested, against a deterministic stub.
/// </summary>
public sealed class OnnxDeepWbCorrector : IDeepWbCorrector, IDisposable
{
    private readonly InferenceSession _session;
    private readonly string _inputName;
    private readonly string _outputName;
    private readonly int _maxSize;

    /// <summary>Default model location: next to the app, as the csproj copies it.</summary>
    public static string DefaultModelPath =>
        Path.Combine(AppContext.BaseDirectory, "models", "net_awb.onnx");

    /// <param name="modelPath">net_awb.onnx; null → <see cref="DefaultModelPath"/>.</param>
    /// <param name="maxSize">Inference long edge. The corrections are per-channel
    /// statistics, so resolution is irrelevant to the answer and 656 keeps the net cheap.</param>
    public OnnxDeepWbCorrector(string? modelPath = null, int maxSize = 656)
    {
        modelPath ??= DefaultModelPath;
        // 正常安装里这个文件是在的。缺失通常意味着有人为了商业再分发主动删掉了
        // models/（那正是 models/README.md 指明的做法），所以这里给的是一句人话而不是断言。
        if (!File.Exists(modelPath))
            throw new FileNotFoundException(
                $"未找到 Deep-WB 模型 net_awb.onnx（应在 {modelPath}）。" +
                "「智能白平衡」需要它，其余功能不受影响；说明见 models/README.md。",
                modelPath);

        _session = new InferenceSession(modelPath);
        _inputName = _session.InputMetadata.Keys.First();
        _outputName = _session.OutputMetadata.Keys.First();
        _maxSize = maxSize;
    }

    /// <summary>
    /// One inference, returning the net's (resized) input paired with its corrected output —
    /// what the affine solve regresses across the density range. No iteration: one pass
    /// captures the net's whole spatial colour decision.
    /// </summary>
    public (ImageBuffer Input, ImageBuffer Output) CorrectOnce(ImageBuffer srgbPositive)
    {
        ImageBuffer inp = ResizeForInference(srgbPositive, _maxSize);
        ImageBuffer outp = Run(inp);
        // deep_wb_correct_once clips its output; auto_wb_onnx deliberately does not.
        for (int i = 0; i < outp.Data.Length; i++)
            outp.Data[i] = Math.Clamp(outp.Data[i], 0f, 1f);
        return (inp, outp);
    }

    /// <summary>
    /// Iterative inference until the per-round gains settle — port of auto_wb_onnx, the
    /// negadoctor-style AUTO. Each round: gains = model(current); current *= gains; cumulate.
    /// </summary>
    /// <param name="image">Linear-light [0,1] post-inversion positive.</param>
    /// <param name="maxIter">Safety cap, NOT the expected stopping point — a normal frame
    /// reaches <paramref name="tol"/> well before it.</param>
    /// <returns>(cumulative_gains, converged). Multiply the ORIGINAL image by the gains to
    /// reproduce the result; converged=false means it hit maxIter and is only approximate.</returns>
    public (double[] Gains, bool Converged) AutoWbGains(ImageBuffer image, int maxIter = 50,
                                                        double tol = 1e-3,
                                                        Action<int, int, double>? progressCb = null)
    {
        ImageBuffer current = ResizeForInference(image, _maxSize);
        var cumulative = new[] { 1.0, 1.0, 1.0 };
        bool converged = false;

        for (int it = 0; it < maxIter; it++)
        {
            ImageBuffer outp = Run(current);
            double[] inMeans = ChannelMeans(current);
            double[] outMeans = ChannelMeans(outp);

            var gains = new double[3];
            for (int c = 0; c < 3; c++)
                gains[c] = (float)(outMeans[c] / Math.Max(inMeans[c], 1e-6));

            for (int c = 0; c < 3; c++) cumulative[c] = (float)(cumulative[c] * gains[c]);
            for (int p = 0; p < current.PixelCount; p++)
                for (int c = 0; c < 3; c++)
                    current.Data[p * 3 + c] = Math.Clamp(current.Data[p * 3 + c] * (float)gains[c], 0f, 1f);

            double maxDev = Math.Max(Math.Abs(gains[0] - 1.0),
                            Math.Max(Math.Abs(gains[1] - 1.0), Math.Abs(gains[2] - 1.0)));
            progressCb?.Invoke(it + 1, maxIter, maxDev);
            if (maxDev < tol) { converged = true; break; }
        }
        return (cumulative, converged);
    }

    /// <summary>
    /// One NN-judged density-domain step for wb_high — port of nn_wb_high_step.
    ///
    /// ⚠ NO CALLERS. The GUI's 智能白平衡 inlines its own version of this step, differing in
    /// one deliberate way: it measures the net's gains over the HIGHLIGHT BAND instead of the
    /// whole-image mean (see MainViewModel.MeanLinearHighlight for why — wb_high is a
    /// highlight-end control, so closing the loop on a whole-image statistic overshoots the
    /// highlight by roughly d_highlight/d_mean and walks real whites off into a cast). Kept as
    /// the faithful record of what Python does; do not wire it up without re-reading that note.
    ///
    /// Derivation across pipeline steps 4→5→6: D_wb = D·wb_high + offset; D_adj =
    /// pivot + (D_wb - pivot)·grade - d_max; T_pos = 10^D_adj. The net asking for gains g
    /// means D_adj += log10(g), hence delta_wb_high = log10(g) / (grade · D). D_highlight
    /// anchors it because wb_high's whole semantic is "neutralise the highlight end".
    /// </summary>
    /// <returns>(delta_wb_high, log_gains). max(|log_gains|) &lt; tol → converged.</returns>
    public (double[] Delta, double[] LogGains) NnWbHighStep(ImageBuffer currentSrgb,
                                                            double[] dHighlight, double grade)
    {
        var (inp, outp) = CorrectOnce(currentSrgb);
        double[] li = LinearChannelMeans(inp);
        double[] lo = LinearChannelMeans(outp);

        var delta = new double[3];
        var logGains = new double[3];
        for (int c = 0; c < 3; c++)
        {
            double gain = Math.Max(lo[c], 1e-8) / Math.Max(li[c], 1e-8);
            logGains[c] = (float)Math.Log10(Math.Max(gain, 1e-8));
            delta[c] = (float)(logGains[c] / (grade * Math.Max(dHighlight[c], 1e-6)));
        }
        return (delta, logGains);
    }

    // ── internals ─────────────────────────────────────────────────────────────────

    /// <summary>Raw inference, NOT clipped — auto_wb_onnx measures the net's unclipped
    /// mean, so clamping here would bias the gains wherever the net overshoots 1.0.
    /// Callers that mirror deep_wb_correct_once clip afterwards.</summary>
    private ImageBuffer Run(ImageBuffer inp)
    {
        int w = inp.Width, h = inp.Height;
        var tensor = new DenseTensor<float>(new[] { 1, 3, h, w });   // NCHW
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                for (int c = 0; c < 3; c++)
                    tensor[0, c, y, x] = inp.Data[(y * w + x) * 3 + c];

        using var results = _session.Run(new[] { NamedOnnxValue.CreateFromTensor(_inputName, tensor) });
        Tensor<float> o = results.First(r => r.Name == _outputName).AsTensor<float>();

        var outImg = new ImageBuffer(w, h);
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                for (int c = 0; c < 3; c++)
                    outImg.Data[(y * w + x) * 3 + c] = o[0, c, y, x];
        return outImg;
    }

    /// <summary>
    /// Resize so max(H,W) ≤ maxSize and both dims are multiples of 16 (the AWB U-Net
    /// requires it) — port of _resize_for_inference.
    ///
    /// Mirrors Python's quantise-to-uint8-then-resample: PIL is handed a uint8 image, so
    /// the 8-bit rounding is part of the reference behaviour, not an artefact to avoid.
    /// The resampler is ImageSharp's Lanczos3 (same a=3 kernel PIL's LANCZOS uses); the
    /// two are not bit-identical and are not expected to be — see the class remarks.
    /// </summary>
    internal static ImageBuffer ResizeForInference(ImageBuffer img, int maxSize)
    {
        int h = img.Height, w = img.Width;
        double scale = Math.Min(1.0, (double)maxSize / Math.Max(h, w));
        int newH = Math.Max(16, (int)(h * scale));
        int newW = Math.Max(16, (int)(w * scale));
        newH = (newH + 15) / 16 * 16;
        newW = (newW + 15) / 16 * 16;

        using var src = new Image<Rgb24>(w, h);
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = (y * w + x) * 3;
                src[x, y] = new Rgb24(To8(img.Data[i]), To8(img.Data[i + 1]), To8(img.Data[i + 2]));
            }

        src.Mutate(c => c.Resize(newW, newH, KnownResamplers.Lanczos3));

        var outImg = new ImageBuffer(newW, newH);
        for (int y = 0; y < newH; y++)
            for (int x = 0; x < newW; x++)
            {
                Rgb24 p = src[x, y];
                int i = (y * newW + x) * 3;
                outImg.Data[i] = p.R / 255.0f;
                outImg.Data[i + 1] = p.G / 255.0f;
                outImg.Data[i + 2] = p.B / 255.0f;
            }
        return outImg;
    }

    // np.clip(img,0,1)*255 -> uint8 (numpy's astype truncates; PIL is fed exactly this).
    private static byte To8(float v) => (byte)(Math.Clamp(v, 0f, 1f) * 255.0f);

    private static double[] ChannelMeans(ImageBuffer img)
    {
        var s = new double[3];
        for (int p = 0; p < img.PixelCount; p++)
            for (int c = 0; c < 3; c++) s[c] += img.Data[p * 3 + c];
        return new[] { s[0] / img.PixelCount, s[1] / img.PixelCount, s[2] / img.PixelCount };
    }

    private static double[] LinearChannelMeans(ImageBuffer srgb)
    {
        float[] inv = Srgb.InverseLut;
        var s = new double[3];
        for (int p = 0; p < srgb.PixelCount; p++)
            for (int c = 0; c < 3; c++) s[c] += inv[Srgb.LutIndex(srgb.Data[p * 3 + c])];
        return new[] { s[0] / srgb.PixelCount, s[1] / srgb.PixelCount, s[2] / srgb.PixelCount };
    }

    public void Dispose() => _session.Dispose();
}
