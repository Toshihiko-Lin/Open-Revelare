using System.Diagnostics;
using System.Globalization;
using OpenRevelare.Core;

// OpenRevelare (C# rewrite) — headless CLI.
//
// Reads a RAW/TIFF negative, runs the density-inversion pipeline, and writes a
// 16-bit positive TIFF or JPEG. Its main job now is verification: the --print-* flags
// below are what tools/parity compares against the frozen Python build.
//
//   OpenRevelare.Cli -i neg.tiff -o pos.tiff --input-srgb --grade 1.65 --d-max 2.0

return Run(args);

static int Run(string[] args)
{
    var opts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    var flags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    for (int i = 0; i < args.Length; i++)
    {
        string a = args[i];
        switch (a)
        {
            case "-i": case "--input": opts["input"] = Next(args, ref i, a); break;
            case "-o": case "--output": opts["output"] = Next(args, ref i, a); break;
            case "--input-srgb": flags.Add("input-srgb"); break;
            case "--intent": opts["intent"] = Next(args, ref i, a); break;
            case "--compress": opts["compress"] = Next(args, ref i, a); break;
            case "--bench": opts["bench"] = Next(args, ref i, a); break;
            case "--decode-only": flags.Add("decode-only"); break;
            case "--t-base": opts["t-base"] = Next(args, ref i, a); break;
            case "--d-max": opts["d-max"] = Next(args, ref i, a); break;
            case "--grade": opts["grade"] = Next(args, ref i, a); break;
            case "--pivot": opts["pivot"] = Next(args, ref i, a); break;
            case "--chroma-grade": opts["chroma-grade"] = Next(args, ref i, a); break;
            case "--scan-exposure-ev": opts["scan-exposure-ev"] = Next(args, ref i, a); break;
            case "--wb-gains": opts["wb-gains"] = Next(args, ref i, a); break;
            case "--exposure": opts["exposure"] = Next(args, ref i, a); break;
            case "--black": opts["black"] = Next(args, ref i, a); break;
            case "--white": opts["white"] = Next(args, ref i, a); break;
            case "--contrast": opts["contrast"] = Next(args, ref i, a); break;
            case "--highlights": opts["highlights"] = Next(args, ref i, a); break;
            case "--shadows": opts["shadows"] = Next(args, ref i, a); break;
            case "--saturation": opts["saturation"] = Next(args, ref i, a); break;
            case "--curve-m": opts["curve-m"] = Next(args, ref i, a); break;
            case "--curve-r": opts["curve-r"] = Next(args, ref i, a); break;
            case "--curve-g": opts["curve-g"] = Next(args, ref i, a); break;
            case "--curve-b": opts["curve-b"] = Next(args, ref i, a); break;
            case "--no-preserve-hue": flags.Add("no-preserve-hue"); break;
            case "--crop": opts["crop"] = Next(args, ref i, a); break;
            case "--rotate": opts["rotate"] = Next(args, ref i, a); break;
            case "--quarter-turns": opts["quarter-turns"] = Next(args, ref i, a); break;
            case "--flip-h": flags.Add("flip-h"); break;
            case "--flip-v": flags.Add("flip-v"); break;
            case "--quality": opts["quality"] = Next(args, ref i, a); break;
            case "--distortion": opts["distortion"] = Next(args, ref i, a); break;
            case "--vignette": opts["vignette"] = Next(args, ref i, a); break;
            case "--vignette-falloff": opts["vignette-falloff"] = Next(args, ref i, a); break;
            case "--lcc": opts["lcc"] = Next(args, ref i, a); break;
            case "--lcc-linear": flags.Add("lcc-linear"); break;
            case "--decouple-matrix": opts["decouple-matrix"] = Next(args, ref i, a); break;
            case "--decouple-mode": opts["decouple-mode"] = Next(args, ref i, a); break;
            case "--decouple-chroma-matrix": opts["decouple-chroma-matrix"] = Next(args, ref i, a); break;
            case "--decouple-chroma-amp": opts["decouple-chroma-amp"] = Next(args, ref i, a); break;
            case "--decouple-cal-r": opts["decouple-cal-r"] = Next(args, ref i, a); break;
            case "--decouple-cal-g": opts["decouple-cal-g"] = Next(args, ref i, a); break;
            case "--decouple-cal-b": opts["decouple-cal-b"] = Next(args, ref i, a); break;
            case "--print-decouple-calib": flags.Add("print-decouple-calib"); break;
            case "--fb-base-rect": opts["fb-base-rect"] = Next(args, ref i, a); break;
            case "--fb-dmax-rect": opts["fb-dmax-rect"] = Next(args, ref i, a); break;
            case "--fb-wb-rect": opts["fb-wb-rect"] = Next(args, ref i, a); break;
            case "--fb-roll": opts["fb-roll"] = Next(args, ref i, a); break;
            case "--fb-roll-values": opts["fb-roll-values"] = Next(args, ref i, a); break;
            case "--fb-sprocket-threshold": opts["fb-sprocket-threshold"] = Next(args, ref i, a); break;
            case "--print-film-base-calib": flags.Add("print-film-base-calib"); break;
            case "--print-wb-calib": flags.Add("print-wb-calib"); break;
            case "--print-trc-calib": flags.Add("print-trc-calib"); break;
            case "--auto-wb": flags.Add("auto-wb"); break;
            case "--awb-model": opts["awb-model"] = Next(args, ref i, a); break;
            case "--print-awb": flags.Add("print-awb"); break;
            case "--dump-preinv": flags.Add("dump-preinv"); break;
            case "--wb-target": opts["wb-target"] = Next(args, ref i, a); break;
            case "--color-space": opts["color-space"] = Next(args, ref i, a); break;
            case "--description": opts["description"] = Next(args, ref i, a); break;
            case "--sprocket": opts["sprocket"] = Next(args, ref i, a); break;
            case "-h": case "--help": PrintUsage(); return 0;
            default:
                Console.Error.WriteLine($"unknown argument: {a}");
                PrintUsage();
                return 2;
        }
    }

    if (!opts.ContainsKey("input") || !opts.ContainsKey("output"))
    {
        Console.Error.WriteLine("error: --input and --output are required");
        PrintUsage();
        return 2;
    }

    var cal = new FrameParams();
    if (opts.TryGetValue("t-base", out var tb)) cal.TBase = ParseTriple(tb);
    if (opts.TryGetValue("d-max", out var dm)) cal.DMax = ParseD(dm);
    if (opts.TryGetValue("grade", out var gr)) cal.Grade = ParseD(gr);
    if (opts.TryGetValue("pivot", out var pv)) cal.Pivot = ParseD(pv);
    if (opts.TryGetValue("chroma-grade", out var cg)) cal.ChromaGrade = ParseD(cg);
    if (opts.TryGetValue("scan-exposure-ev", out var se)) cal.ScanExposureEv = ParseD(se);
    if (opts.TryGetValue("wb-gains", out var wg)) cal.WbGains = ParseTriple(wg);
    if (opts.TryGetValue("exposure", out var exv)) cal.ExposureEv = ParseD(exv);
    if (opts.TryGetValue("black", out var bk)) cal.BlackPoint = ParseD(bk);
    if (opts.TryGetValue("white", out var wt)) cal.WhitePoint = ParseD(wt);
    if (opts.TryGetValue("contrast", out var ct)) cal.Contrast = ParseD(ct);
    if (opts.TryGetValue("highlights", out var hl)) cal.Highlights = ParseD(hl);
    if (opts.TryGetValue("shadows", out var sd)) cal.Shadows = ParseD(sd);
    if (opts.TryGetValue("saturation", out var sat)) cal.Saturation = ParseD(sat);
    if (opts.TryGetValue("curve-m", out var cm2)) cal.CurvePointsM = ParseCurve(cm2);
    if (opts.TryGetValue("curve-r", out var cr2)) cal.CurvePointsR = ParseCurve(cr2);
    if (opts.TryGetValue("curve-g", out var cg2)) cal.CurvePointsG = ParseCurve(cg2);
    if (opts.TryGetValue("curve-b", out var cb2)) cal.CurvePointsB = ParseCurve(cb2);
    if (flags.Contains("no-preserve-hue")) cal.CurvePreserveHue = false;
    if (opts.TryGetValue("crop", out var cp))
    {
        var q = ParseTriple4(cp);
        cal.CropRect = (q[0], q[1], q[2], q[3]);
    }
    if (opts.TryGetValue("rotate", out var rot)) cal.Rotation = ParseD(rot);
    if (opts.TryGetValue("quarter-turns", out var qt)) cal.QuarterTurns = int.Parse(qt);
    if (flags.Contains("flip-h")) cal.FlipH = true;
    if (flags.Contains("flip-v")) cal.FlipV = true;
    if (opts.TryGetValue("distortion", out var dk)) cal.DistortionK1 = ParseD(dk);
    if (opts.TryGetValue("vignette", out var vg)) cal.VignetteAmount = ParseD(vg);
    if (opts.TryGetValue("vignette-falloff", out var vf)) cal.VignetteFalloff = ParseD(vf);
    if (opts.TryGetValue("decouple-matrix", out var dcm)) cal.DecoupleMatrix = ParseMatrix3(dcm);
    if (opts.TryGetValue("decouple-mode", out var dcmode))
        cal.DecoupleMode = dcmode.Equals("density", StringComparison.OrdinalIgnoreCase)
            ? DecoupleMode.Density : DecoupleMode.Linear;
    if (opts.TryGetValue("decouple-chroma-matrix", out var dccm)) cal.DecoupleChromaMatrix = ParseMatrix3(dccm);
    if (opts.TryGetValue("decouple-chroma-amp", out var dca)) cal.DecoupleChromaAmp = ParseTriple(dca);
    if (opts.TryGetValue("sprocket", out var sp))
    {
        cal.SprocketEnabled = true;
        cal.SprocketThreshold = ParseD(sp);
    }
    cal.OutputIntent = opts.TryGetValue("intent", out var it) && it.Equals("none", StringComparison.OrdinalIgnoreCase)
        ? OutputIntent.None
        : OutputIntent.Basic;

    try
    {
        cal.Validate();

        if (opts.TryGetValue("lcc", out var lccPath))
            cal.LccFlatField = Lcc.LoadFlatField(lccPath, flags.Contains("lcc-linear"));

        // Path-A decouple calibration diagnostic: compute M_linear / M_density /
        // chroma_amp / chroma_matrix from R/G/B cal frames (+ content = -i) and print.
        if (flags.Contains("print-decouple-calib"))
        {
            ImageBuffer calR = LoadLinear(opts["decouple-cal-r"]);
            ImageBuffer calG = LoadLinear(opts["decouple-cal-g"]);
            ImageBuffer calB = LoadLinear(opts["decouple-cal-b"]);
            ImageBuffer content = LoadLinear(opts["input"]);

            double[,] mLin = DecoupleCalibration.ComputeDecoupleMatrix(calR, calG, calB);
            double[,] mDen = DecoupleCalibration.ComputeDensityMatrix(calR, calG, calB);

            ImageBuffer post = DecoupleCalibration.ApplyMatrixClip(content, mLin);
            double[] amp = DecoupleCalibration.ChromaAmplificationPerChannel(content, post);
            double[,] cMat = DecoupleCalibration.ChromaAxisCompensationMatrix(content, post);

            Console.WriteLine("M_linear " + Fmt9(mLin));
            Console.WriteLine("M_density " + Fmt9(mDen));
            Console.WriteLine("chroma_amp " + string.Join(",", amp.Select(x => x.ToString("R", CultureInfo.InvariantCulture))));
            Console.WriteLine("chroma_matrix " + Fmt9(cMat));

            var pd = DecoupleCalibration.ComputeDecoupleParams(mDen, content);
            Console.WriteLine("params_density " + Fmt3(new[] { pd.Alpha, pd.ChromaAmp }));
            var pl = DecoupleCalibration.ComputeDecoupleParamsLinear(mLin, content);
            Console.WriteLine("params_linear " + Fmt3(new[] { pl.Alpha, pl.ChromaAmp }));
            Console.WriteLine("params_linear_amp " + Fmt3(pl.AmpPerChannel));

            // Content-based R/G/B assignment: argmax over each file's centre-ROI mean.
            // Fed the cal frames in R,G,B order, so a correct identify returns 0,1,2.
            var (ir, ig, ib) = DecoupleCalibration.IdentifyRgbIndices(
                new[] { DecoupleCalibration.RoiMean(calR), DecoupleCalibration.RoiMean(calG),
                        DecoupleCalibration.RoiMean(calB) });
            Console.WriteLine("identify_rgb " + Fmt3(new double[] { ir, ig, ib }));
            return 0;
        }

        // Transfer-function diagnostic. Probes the TRCs on an exact ramp rather than
        // through an exported file: AdobeRGB's power curve is so steep near 0 that ONE
        // linear 16-bit LSB is ~137 encoded levels, so a file-level comparison drowns the
        // TRC in the input's own quantisation and can prove nothing.
        if (flags.Contains("print-trc-calib"))
        {
            const int N = 4096;
            var probe = new float[N];
            for (int k = 0; k < N; k++) probe[k] = k / (N - 1.0f);

            var adobe = (float[])probe.Clone();
            Srgb.ApplyAdobeRgbInPlace(adobe);
            var srgbEnc = (float[])probe.Clone();
            Srgb.ApplyForwardInPlace(srgbEnc);
            var linear = (float[])probe.Clone();
            Srgb.ApplyInverseInPlace(linear);

            Console.WriteLine("trc_adobe " + string.Join(",", adobe.Select(x => x.ToString("R", CultureInfo.InvariantCulture))));
            Console.WriteLine("trc_srgb " + string.Join(",", srgbEnc.Select(x => x.ToString("R", CultureInfo.InvariantCulture))));
            Console.WriteLine("trc_srgb_inv " + string.Join(",", linear.Select(x => x.ToString("R", CultureInfo.InvariantCulture))));
            return 0;
        }

        // Deep-WB (ONNX) diagnostic: run the REAL net through the same iterative affine
        // solve the stub is parity-tested against. Not bit-comparable with Python (the net
        // depends on the onnxruntime build and the pre-resize on PIL vs ImageSharp); the
        // bar is that the converged wb_high lands in the same ballpark — see ref_awb.py.
        if (flags.Contains("print-awb"))
        {
            ImageBuffer srgb = TiffIO.LoadTiff(opts["input"], inputIsSrgb: false);
            using var onnx = new OpenRevelare.DeepWb.Onnx.OnnxDeepWbCorrector(
                opts.TryGetValue("awb-model", out var mp) ? mp : null);

            var (inp, outp) = onnx.CorrectOnce(srgb);
            Console.WriteLine("resized " + Fmt3(new double[] { inp.Width, inp.Height }));
            Console.WriteLine("net_in_mean " + Fmt3(MeanPixels(inp)));
            Console.WriteLine("net_out_mean " + Fmt3(MeanPixels(outp)));

            var g = onnx.AutoWbGains(srgb);
            Console.WriteLine("auto_gains " + Fmt3(g.Gains));
            Console.WriteLine("auto_converged " + Fmt1(g.Converged ? 1 : 0));

            var ai = WhiteBalance.AutoWbAffineIterative(srgb, onnx, cal.Grade, cal.Pivot, cal.DMax);
            Console.WriteLine("affine_iter_high " + Fmt3(ai.WbHigh));
            Console.WriteLine("affine_iter_offset " + Fmt3(ai.WbOffset));
            Console.WriteLine("affine_iter_flags " + Fmt3(new double[] { ai.Converged ? 1 : 0, ReasonCode(ai.Reason) }));
            return 0;
        }

        // white_balance.py diagnostic: the runtime-independent half — density round-trip,
        // affine solve, and the full iterative loop driven by GreyWorldCorrector (the same
        // deterministic stub tools/parity/ref_wb.py monkeypatches in, so the loop's
        // composition / gm-normalisation / plateau logic is verifiable without ONNX).
        if (flags.Contains("print-wb-calib"))
        {
            ImageBuffer inSrgb = LoadLinear(opts["input"]);
            ImageBuffer targetSrgb = LoadLinear(opts["wb-target"]);
            double grade = cal.Grade, pivot = cal.Pivot, dMax = cal.DMax;
            double cgrade = cal.ChromaGrade;
            double[] ccs = cal.ChromaChannelScale;

            double[][] dSimple = WhiteBalance.SrgbToPreStep4Density(inSrgb, grade, pivot, dMax);
            double[][] dChroma = WhiteBalance.SrgbToPreStep4Density(inSrgb, grade, pivot, dMax, cgrade, ccs);
            Console.WriteLine("d_simple_mean " + Fmt3(MeanRows(dSimple)));
            Console.WriteLine("d_chroma_mean " + Fmt3(MeanRows(dChroma)));

            // Round-trip both paths back to sRGB: exercises PreStep4DensityToSrgb.
            ImageBuffer rtS = WhiteBalance.PreStep4DensityToSrgb(dSimple, inSrgb.Width, inSrgb.Height, grade, pivot, dMax);
            ImageBuffer rtC = WhiteBalance.PreStep4DensityToSrgb(dChroma, inSrgb.Width, inSrgb.Height, grade, pivot, dMax, cgrade, ccs);
            Console.WriteLine("roundtrip_simple_mean " + Fmt3(MeanPixels(rtS)));
            Console.WriteLine("roundtrip_chroma_mean " + Fmt3(MeanPixels(rtC)));

            var s1 = WhiteBalance.SolveWbAffineFromPositive(inSrgb, targetSrgb, grade, pivot, dMax);
            Console.WriteLine("affine_high " + Fmt3(s1.WbHigh));
            Console.WriteLine("affine_offset " + Fmt3(s1.WbOffset));
            Console.WriteLine("affine_ok " + Fmt1(s1.Ok ? 1 : 0));

            var s2 = WhiteBalance.SolveWbAffineFromPositive(inSrgb, targetSrgb, grade, pivot, dMax, cgrade, ccs);
            Console.WriteLine("affine_chroma_high " + Fmt3(s2.WbHigh));
            Console.WriteLine("affine_chroma_offset " + Fmt3(s2.WbOffset));
            Console.WriteLine("affine_chroma_ok " + Fmt1(s2.Ok ? 1 : 0));

            var corrector = new GreyWorldCorrector();
            var it1 = WhiteBalance.AutoWbAffineIterative(inSrgb, corrector, grade, pivot, dMax);
            Console.WriteLine("iter_high " + Fmt3(it1.WbHigh));
            Console.WriteLine("iter_offset " + Fmt3(it1.WbOffset));
            Console.WriteLine("iter_flags " + Fmt3(new double[] { it1.Converged ? 1 : 0, ReasonCode(it1.Reason) }));

            var it2 = WhiteBalance.AutoWbAffineIterative(inSrgb, corrector, grade, pivot, dMax,
                                                         wbOffsetEnabled: true);
            Console.WriteLine("iter_off_high " + Fmt3(it2.WbHigh));
            Console.WriteLine("iter_off_offset " + Fmt3(it2.WbOffset));
            Console.WriteLine("iter_off_flags " + Fmt3(new double[] { it2.Converged ? 1 : 0, ReasonCode(it2.Reason) }));

            Console.WriteLine("gains_mean " + Fmt3(MeanPixels(WhiteBalance.ApplyWbGains(inSrgb, new[] { 1.1, 0.95, 1.3 }))));
            return 0;
        }

        // film_base.py diagnostic: sample T_base / D_max / wb_offset / wb_high from
        // rects on -i, and optionally T_base across a roll. Sampling order below is
        // the mandated one (offset before high) — see FilmBase's class remarks.
        if (flags.Contains("print-film-base-calib"))
        {
            ImageBuffer content = LoadLinear(opts["input"]);
            double[]? tBase = null;

            if (opts.TryGetValue("fb-base-rect", out var br))
            {
                tBase = FilmBase.SampleTBase(content, ParseRect(br));
                Console.WriteLine("t_base " + Fmt3(tBase));
            }
            if (opts.TryGetValue("fb-roll", out var roll))
            {
                var frames = roll.Split(';', StringSplitOptions.RemoveEmptyEntries).Select(LoadLinear).ToList();
                var vals = opts.TryGetValue("fb-roll-values", out var rv)
                    ? rv.Split(';', StringSplitOptions.RemoveEmptyEntries).Select(LoadLinear).ToList()
                    : null;
                double? thr = opts.TryGetValue("fb-sprocket-threshold", out var ft) ? ParseD(ft) : null;
                Console.WriteLine("t_base_roll " + Fmt3(FilmBase.EstimateTBaseFromRoll(frames, thr, vals)));
                // No threshold → the pure-brightness branch (p99.99, values from the
                // frames themselves). Different percentile AND different code path.
                Console.WriteLine("t_base_roll_nomask " + Fmt3(FilmBase.EstimateTBaseFromRoll(frames)));

                // Per-frame histogram estimators (sprocket.py), then [FB-AUTO] auto-WB.
                Console.WriteLine("sprocket_threshold " +
                    Fmt3(frames.Select(Sprocket.EstimateSprocketThreshold).ToArray()));
                Console.WriteLine("dark_valley " +
                    Fmt3(frames.Select(Sprocket.EstimateDarkValley).ToArray()));
                Console.WriteLine("board_level " +
                    Fmt3(frames.Select(f2 => Sprocket.MeasureBoardAndFilmbase(f2).BoardLevel).ToArray()));
                Console.WriteLine("filmbase_hi " +
                    Fmt3(frames.Select(f2 => Sprocket.MeasureBoardAndFilmbase(f2).FilmbaseHighlight).ToArray()));
                Console.WriteLine("brightest_frame " + Fmt1(Sprocket.BrightestFilmbaseFrame(frames)));

                if (tBase != null)
                {
                    Console.WriteLine("auto_wb_high " +
                        Fmt3(FilmBase.AutoWbHighFromRoll(frames, tBase, null, thr, vals)));
                    Console.WriteLine("auto_wb_high_nomask " +
                        Fmt3(FilmBase.AutoWbHighFromRoll(frames, tBase)));
                }
            }
            if (tBase != null)
            {
                double[] tbase = tBase;
                var tNorm = new ImageBuffer(content.Width, content.Height);
                for (int p = 0; p < content.PixelCount; p++)
                    for (int c = 0; c < 3; c++)
                        tNorm.Data[p * 3 + c] = (float)(content.Data[p * 3 + c] / Math.Max(tbase[c], 1e-10));
                Console.WriteLine("d_max_detect " + Fmt1(FilmBase.DetectDMax(tNorm)));

                if (opts.TryGetValue("fb-dmax-rect", out var dr))
                    Console.WriteLine("d_max_rect " + Fmt1(FilmBase.SampleDMaxFromRect(content, ParseRect(dr), tbase)));
                if (opts.TryGetValue("fb-wb-rect", out var wr))
                {
                    var rect = ParseRect(wr);
                    double[] wbOffset = FilmBase.SampleWbOffsetFromRect(content, rect, tbase);
                    double[] wbHigh = FilmBase.SampleWbHighFromRect(content, rect, tbase, wbOffset);
                    Console.WriteLine("wb_offset " + Fmt3(wbOffset));
                    Console.WriteLine("wb_high " + Fmt3(wbHigh));
                    // Offset-free solve (white-light rolls). In the paired order above the
                    // offset has already flattened the channels, so wb_high is exactly
                    // 1,1,1 and exercises none of the solve — this key does.
                    Console.WriteLine("wb_high_solo " + Fmt3(FilmBase.SampleWbHighFromRect(content, rect, tbase)));
                }
            }
            return 0;
        }

        // Dump the pre-inversion chain only (distortion -> lcc -> vignette), so a
        // pre-inversion operator can be compared in the LINEAR domain. The inversion
        // amplifies sub-LSB pre-inversion differences to 13-40 LSB in the highlights, so a
        // full-chain max says NOTHING about whether such an operator is correct — the
        // isolated linear-domain diff is the only real verdict (HANDOFF §4). Kept as a
        // permanent flag rather than the throwaway branch §4 suggests: it has been needed
        // twice now (B batch, then distortion), and re-adding it each time invites getting
        // the stage order subtly wrong.
        if (flags.Contains("dump-preinv"))
        {
            ImageBuffer pre = TiffIO.LoadTiff(opts["input"], flags.Contains("input-srgb"));
            if (cal.DistortionK1 != 0.0) pre = LensCorrections.ApplyDistortion(pre, cal.DistortionK1);
            if (cal.LccFlatField is not null) Lcc.Apply(pre.Data, pre.Width, pre.Height, cal.LccFlatField);
            if (cal.VignetteAmount != 0.0)
                LensCorrections.ApplyVignette(pre.Data, pre.Width, pre.Height, cal.VignetteAmount, cal.VignetteFalloff);
            TiffIO.ExportTiff16(pre, opts["output"], TiffIO.CompressionMode.None);
            Console.WriteLine($"wrote pre-inversion dump {opts["output"]}");
            return 0;
        }

        var sw = Stopwatch.StartNew();
        bool isRaw = RawDecode.IsRawExtension(opts["input"]);
        ImageBuffer img = isRaw
            ? RawDecode.DecodeRaw(opts["input"])
            : TiffIO.LoadTiff(opts["input"], flags.Contains("input-srgb"));
        double tLoad = sw.Elapsed.TotalMilliseconds;
        Console.WriteLine($"loaded {(isRaw ? "RAW" : "TIFF")} {img.Width}×{img.Height} ({img.PixelCount / 1e6:F1} MP) in {tLoad:F1} ms");

        // Decode-only: write the linear camera-native array as-is (parity check vs rawpy).
        if (flags.Contains("decode-only"))
        {
            TiffIO.ExportTiff16(img, opts["output"], TiffIO.CompressionMode.None);
            Console.WriteLine($"wrote decoded linear {opts["output"]}");
            return 0;
        }

        ImageBuffer outImg;
        if (opts.TryGetValue("bench", out var bn) && int.TryParse(bn, out int iters) && iters > 0)
        {
            // Warm-up (JIT + first-touch), then N timed in-process runs; report min/median.
            outImg = Pipeline.ProcessFrame(img, cal);
            var times = new double[iters];
            for (int k = 0; k < iters; k++)
            {
                var s = Stopwatch.StartNew();
                outImg = Pipeline.ProcessFrame(img, cal);
                times[k] = s.Elapsed.TotalMilliseconds;
            }
            Array.Sort(times);
            double median = times[iters / 2];
            Console.WriteLine($"process x{iters}: min {times[0]:F1} ms | median {median:F1} ms | max {times[iters - 1]:F1} ms");
        }
        else
        {
            sw.Restart();
            outImg = Pipeline.ProcessFrame(img, cal);
            Console.WriteLine($"processed in {sw.Elapsed.TotalMilliseconds:F1} ms");
        }

        var compress = opts.TryGetValue("compress", out var cv)
            ? cv.ToLowerInvariant() switch
            {
                "none" => TiffIO.CompressionMode.None,
                "deflate" => TiffIO.CompressionMode.Deflate,
                _ => TiffIO.CompressionMode.Lzw,
            }
            : TiffIO.CompressionMode.Lzw;

        sw.Restart();
        string outPath = opts["output"];
        string ext = Path.GetExtension(outPath).ToLowerInvariant();
        if (ext is ".jpg" or ".jpeg")
        {
            int quality = opts.TryGetValue("quality", out var qv) ? int.Parse(qv) : 95;
            JpegIO.ExportJpeg(outImg, outPath, quality);
            Console.WriteLine($"wrote {outPath} (JPEG q{quality}) in {sw.Elapsed.TotalMilliseconds:F1} ms");
        }
        else
        {
            // --color-space applies the working space's TRC and embeds its ICC. It is only
            // meaningful on --intent none, where the pipeline hands us LINEAR data; with
            // intent basic the sRGB TRC is already baked in, so we only tag it (mirroring
            // export.py's already_encoded branch, which likewise ignores color_space).
            ColorSpace? icc = null;
            if (cal.OutputIntent == OpenRevelare.Core.OutputIntent.Basic)
            {
                icc = ColorSpace.Srgb;
                if (opts.ContainsKey("color-space"))
                    Console.WriteLine("note: --color-space ignored (intent basic already encodes sRGB)");
            }
            else if (opts.TryGetValue("color-space", out var csName))
            {
                icc = csName.Equals("adobergb", StringComparison.OrdinalIgnoreCase)
                    ? ColorSpace.AdobeRgb : ColorSpace.Srgb;
                if (icc == ColorSpace.AdobeRgb) Srgb.ApplyAdobeRgbInPlace(outImg.Data);
                else Srgb.ApplyForwardInPlace(outImg.Data);
            }
            opts.TryGetValue("description", out var desc);
            TiffIO.ExportTiff16(outImg, outPath, compress, icc, desc);
            Console.WriteLine($"wrote {outPath} (TIFF {compress}{(icc is null ? "" : $", ICC {icc}")}) "
                            + $"in {sw.Elapsed.TotalMilliseconds:F1} ms");
        }
        return 0;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"error: {ex.Message}");
        return 1;
    }
}

// Load a calibration/content image as linear-light (RAW → UniWB decode; TIFF → linear).
static ImageBuffer LoadLinear(string path)
    => RawDecode.IsRawExtension(path) ? RawDecode.DecodeRaw(path) : TiffIO.LoadTiff(path, inputIsSrgb: false);

// '' → 0, 'guard' → 1, 'maxiter' → 2 (compare_calib.py compares numbers, not strings).
static double ReasonCode(string reason) => reason switch { "" => 0, "guard" => 1, _ => 2 };

// Per-channel mean of an (N,3) density array / an image — compact parity signatures for
// arrays too big to print in full.
static double[] MeanRows(double[][] rows)
{
    var s = new double[3];
    foreach (var r in rows) { s[0] += r[0]; s[1] += r[1]; s[2] += r[2]; }
    return new[] { s[0] / rows.Length, s[1] / rows.Length, s[2] / rows.Length };
}

static double[] MeanPixels(ImageBuffer img)
{
    var s = new double[3];
    for (int p = 0; p < img.PixelCount; p++)
        for (int c = 0; c < 3; c++) s[c] += img.Data[p * 3 + c];
    return new[] { s[0] / img.PixelCount, s[1] / img.PixelCount, s[2] / img.PixelCount };
}

// Normalised sampling rect "x,y,w,h" → tuple.
static (double X, double Y, double W, double H) ParseRect(string s)
{
    var q = ParseTriple4(s);
    return (q[0], q[1], q[2], q[3]);
}

static string Fmt3(double[] v) => string.Join(",", v.Select(x => x.ToString("R", CultureInfo.InvariantCulture)));

static string Fmt1(double v) => v.ToString("R", CultureInfo.InvariantCulture);

// Row-major 3×3 → "m00,m01,...,m22" round-trippable doubles.
static string Fmt9(double[,] m)
{
    var sb = new System.Text.StringBuilder();
    for (int r = 0; r < 3; r++)
        for (int c = 0; c < 3; c++)
        {
            if (r + c > 0) sb.Append(',');
            sb.Append(m[r, c].ToString("R", CultureInfo.InvariantCulture));
        }
    return sb.ToString();
}

static string Next(string[] args, ref int i, string flag)
{
    if (i + 1 >= args.Length) throw new ArgumentException($"missing value for {flag}");
    return args[++i];
}

static double ParseD(string s) => double.Parse(s, CultureInfo.InvariantCulture);

static double[] ParseTriple(string s)
{
    var parts = s.Split(',');
    if (parts.Length != 3) throw new ArgumentException($"expected 3 comma-separated numbers, got {parts.Length}");
    return new[] { ParseD(parts[0].Trim()), ParseD(parts[1].Trim()), ParseD(parts[2].Trim()) };
}

// 3×3 row-major matrix: "m00,m01,m02,m10,m11,m12,m20,m21,m22" → double[3,3].
static double[,] ParseMatrix3(string s)
{
    var parts = s.Split(',');
    if (parts.Length != 9) throw new ArgumentException($"expected 9 comma-separated numbers, got {parts.Length}");
    var m = new double[3, 3];
    for (int r = 0; r < 3; r++)
        for (int c = 0; c < 3; c++)
            m[r, c] = ParseD(parts[r * 3 + c].Trim());
    return m;
}

static double[] ParseTriple4(string s)
{
    var parts = s.Split(',');
    if (parts.Length != 4) throw new ArgumentException($"expected 4 comma-separated numbers, got {parts.Length}");
    return new[] { ParseD(parts[0].Trim()), ParseD(parts[1].Trim()), ParseD(parts[2].Trim()), ParseD(parts[3].Trim()) };
}

// Curve control points: "x1,y1;x2,y2;..." → list of (x,y).
static List<(double X, double Y)> ParseCurve(string s)
{
    var pts = new List<(double, double)>();
    foreach (var pair in s.Split(';', StringSplitOptions.RemoveEmptyEntries))
    {
        var xy = pair.Split(',');
        if (xy.Length != 2) throw new ArgumentException($"bad curve point '{pair}' (want x,y)");
        pts.Add((ParseD(xy[0].Trim()), ParseD(xy[1].Trim())));
    }
    return pts;
}

static void PrintUsage()
{
    Console.WriteLine(
        "Usage: OpenRevelare.Cli -i <in.tiff> -o <out.tiff> [options]\n" +
        "  -i, --input <path>          input negative TIFF (RGB, 8/16-bit)\n" +
        "  -o, --output <path>         output positive TIFF (16-bit RGB)\n" +
        "  --input-srgb                treat input as sRGB-gamma (linearise on load)\n" +
        "  --intent <basic|none>       output intent (default: basic)\n" +
        "  --t-base <r,g,b>            film base transmittance (e.g. 0.82,0.51,0.29)\n" +
        "  --d-max <v>                 physical max density\n" +
        "  --grade <v>                 density-domain contrast (paper grade)\n" +
        "  --pivot <v>                 mid-tone anchor\n" +
        "  --chroma-grade <v>          density-domain chroma scale\n" +
        "  --scan-exposure-ev <v>      density-domain exposure bias (EV)\n" +
        "  --lcc <path>                LCC flat-field reference (RAW/TIFF); per-channel divide\n" +
        "  --lcc-linear                treat the LCC TIFF as linear (default: sRGB gamma)\n" +
        "  --decouple-matrix <9 vals>  Path-A decouple 3×3 (row-major m00,m01,...,m22)\n" +
        "  --decouple-mode <linear|density>  decouple domain (default: linear)\n" +
        "  --decouple-chroma-matrix <9 vals> chroma-compensation 3×3 fed into inversion\n" +
        "  --decouple-chroma-amp <r,g,b>     per-channel chroma amp fed into inversion\n" +
        "  --decouple-cal-r/-g/-b <path>     R/G/B light-source calibration frames\n" +
        "  --print-decouple-calib            compute+print Path-A matrices from cal frames, exit\n" +
        "  --print-film-base-calib           sample+print film_base params from -i, exit\n" +
        "  --fb-base-rect <x,y,w,h>          T_base rect (normalised); enables d_max/wb output\n" +
        "  --fb-dmax-rect <x,y,w,h>          D_max rect (a fully-exposed / shadow area)\n" +
        "  --fb-wb-rect <x,y,w,h>            neutral rect → wb_offset then wb_high\n" +
        "  --fb-roll <p1;p2;...>             roll frames → t_base_roll (median consensus)\n" +
        "  --fb-roll-values <p1;p2;...>      post-decouple values for the roll (masks stay on --fb-roll)\n" +
        "  --fb-sprocket-threshold <v>       board↔base luma cut for --fb-roll");
}

/// <summary>
/// Deterministic stand-in for the Deep-WB net: grey-world gains, no resize. Exists so the
/// AutoWbAffineIterative loop can be parity-checked against Python — the real ONNX net can
/// never be bit-matched across onnxruntime/PIL and C#, but the loop around it is pure
/// arithmetic and must be. tools/parity/ref_wb.py monkeypatches the identical function over
/// deep_wb_correct_once.
/// </summary>
sealed class GreyWorldCorrector : IDeepWbCorrector
{
    public (ImageBuffer Input, ImageBuffer Output) CorrectOnce(ImageBuffer srgbPositive)
    {
        int n = srgbPositive.PixelCount;
        var inp = new ImageBuffer(srgbPositive.Width, srgbPositive.Height);
        for (int i = 0; i < inp.Data.Length; i++)
            inp.Data[i] = Math.Clamp(srgbPositive.Data[i], 0.0f, 1.0f);

        // float32 sequential accumulation — matches numpy's mean(axis=0) on (N,3) float32.
        float m0 = 0, m1 = 0, m2 = 0;
        for (int p = 0; p < n; p++) { m0 += inp.Data[p * 3]; m1 += inp.Data[p * 3 + 1]; m2 += inp.Data[p * 3 + 2]; }
        m0 /= (float)n; m1 /= (float)n; m2 /= (float)n;
        float grey = (m0 + m1 + m2) / 3.0f;
        float g0 = grey / MathF.Max(m0, 1e-6f), g1 = grey / MathF.Max(m1, 1e-6f), g2 = grey / MathF.Max(m2, 1e-6f);

        var outp = new ImageBuffer(srgbPositive.Width, srgbPositive.Height);
        for (int p = 0; p < n; p++)
        {
            outp.Data[p * 3] = Math.Clamp(inp.Data[p * 3] * g0, 0.0f, 1.0f);
            outp.Data[p * 3 + 1] = Math.Clamp(inp.Data[p * 3 + 1] * g1, 0.0f, 1.0f);
            outp.Data[p * 3 + 2] = Math.Clamp(inp.Data[p * 3 + 2] * g2, 0.0f, 1.0f);
        }
        return (inp, outp);
    }
}
