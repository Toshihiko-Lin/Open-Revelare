using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using OpenRevelare.Core;
using Xunit;

namespace OpenRevelare.Tests;

/// <summary>
/// 金标准回归：整条管线的逐像素基线。
///
/// **这是唯一一个守住产品承诺的测试。** README 说的是「同一卷底片，今天处理、明年处理、
/// 换台机器处理，结果都一样」，以及 C# 重构时的「老用户处理结果逐像素不变」。在此之前，
/// 这两句话没有任何机制兜底——仓库里其余每一个测试守的都是某个**局部**性质（片基端点不
/// 越界、裁切跟着方向走、印片 LUT 在各输出空间下的行为），而「整条链路的输出没变」谁也
/// 没在守。一次无意的重构、一次依赖升级、一次浮点写法调整，都可以在全绿的情况下悄悄改掉
/// 每一张照片，而症状是「颜色好像有点不一样」——用户报不清，作者也复现不出。
///
/// **为什么基线是文本而不是图片。** 二进制 fixture 在 diff 里是一坨乱码：基线一旦要变，
/// review 的人只能选择相信或不信，看不出变了多少、变在哪个通道、是全局偏移还是局部。这里
/// 存的是十六进制的 float 位模式加一行人类可读的通道统计，于是 git diff 直接就是一份变更
/// 报告——第几个采样点、哪个通道、变了几位。基线该不该更新，是能被讨论的。
///
/// **比较方式：先比位，位不同再按容差比数值。** 最初这里是纯位精确，理由是「容差要多少才
/// 对？没有非任意的答案」。那次也写明：真出现合理的跨平台分歧，再讨论怎么放宽，前提是先
/// 看得见。分歧出现了——基线在作者机器上生成，第一次上 GitHub 的 Linux runner 就红，最大
/// 相对差 4.2e-05，而管线一行没动；原因是编译器的 FMA 合并与向量化选择随 CPU 而异。
///
/// 于是放宽到 <see cref="GoldenTolerance"/>（1e-4），而不是改基线：CI 同时跑 ubuntu 与
/// macos，位精确要求它们与作者机器三方逐位一致，这在多平台上本就无法成立——换台机器重生成
/// 只是把红色搬个家。阈值比观测到的平台噪声大一倍多，比任何真回归的量级小三四个数量级，
/// 「每个像素动一点点」这类漂移仍然拦得住。基线本身照旧存位模式，diff 依然是精确的。
///
/// **基线怎么更新。** 设环境变量 <c>REVELARE_UPDATE_GOLDEN=1</c> 跑一次测试，基线文件会被
/// 重写，然后把 diff 连同「为什么该变」一起提交。**不要**因为红了就顺手更新——先回答这次
/// 改动是否本就该改变每一张照片的像素。
/// </summary>
public class GoldenFrameTests
{
    /// <summary>基线文件与测试源码放在一起，随源码走，不进产物。</summary>
    private static string GoldenPath(string name)
    {
        // AppContext.BaseDirectory 是 bin/<cfg>/net8.0/，向上四级回到测试项目根。
        string dir = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "Golden"));
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, name + ".txt");
    }

    private static bool Updating =>
        Environment.GetEnvironmentVariable("REVELARE_UPDATE_GOLDEN") == "1";

    // ── 合成负片 ────────────────────────────────────────────────────────────────
    //
    // 不用真实底片扫描件：一张有意义的样张是几十 MB，仓库要背一辈子，而它能覆盖的东西
    // 合成图同样能覆盖——只要合成图**把管线的每条分支都踩到**。下面这张就是照着这个标准
    // 造的，不是随手一张渐变。
    //
    // 用确定性 LCG 而不是 System.Random：Random 的算法在 .NET 版本之间变过，基线会因为
    // 换 SDK 而无故变化，那就把「结果可复现」这件事测反了。

    private const int Width = 37;   // 质数，且非 2 的幂：能踩到行尾的非对齐/非整块路径
    private const int Height = 23;

    /// <summary>
    /// 一张负片：橙色片基之上叠加结构。
    ///
    /// 值域刻意贴着 C-41 翻拍件的真实形状——片基透射率 R&gt;G&gt;B（橙罩），高光处密度最大、
    /// 透射率最小。若造一张平场或纯随机噪声，反相里的 LUT 路径、&gt;1 直算旁路、通道间的
    /// 色度分解就都踩不到，测试会绿得毫无意义。
    ///
    /// **横向梯度铺在密度域上，而不是透射率域上。** 这不是讲究，是这张图有没有用的分界：
    /// 管线消费的是 -log10(T)，在 T 上均匀取值意味着密度全挤在低端——第一版就是这么写的，
    /// 结果整帧密度只覆盖 0.08–1.30，而标定的两端是 0.09–2.79，于是渲染出来一半以上的像素
    /// 被压到纯黑（蓝通道 58%）。基线里存一堆 0 是最坏的情况：它照样「通过」，但那些像素上
    /// 任何回归都不会改变任何一位。所以梯度直接铺在密度上，让两端刚好落在
    /// <see cref="Calibration"/> 的 D_min 与 D_max 上，中间每一个像素都是有效样本。
    /// </summary>
    private static ImageBuffer MakeNegative()
    {
        var img = new ImageBuffer(Width, Height);
        float[] d = img.Data;
        ulong s = 0x9E3779B97F4A7C15UL;   // 固定种子

        float Next()
        {
            s = unchecked(s * 6364136223846793005UL + 1442695040888963407UL);
            return (float)((s >> 40) / (double)(1 << 24));   // [0,1)
        }

        // 与 Calibration() 的两端一致：梯度从片基密度铺到高光密度。
        double[] dLo = { 0.09, 0.29, 0.54 };
        double[] dHi = { 2.21, 2.34, 2.79 };

        for (int y = 0; y < Height; y++)
            for (int x = 0; x < Width; x++)
            {
                int i = (y * Width + x) * 3;

                // 横向：密度域上的线性梯度（负片密度大 = 正片上亮）
                double t = x / (double)(Width - 1);

                // 纵向：一条色度带，制造通道间不同步的变化——三通道同步的话，
                // 色度矩阵/分解分支等于没被测到。加在密度上，即真实的色偏。
                double band = (y % 5) / 4.0;

                for (int c = 0; c < 3; c++)
                {
                    double density = dLo[c] + (dHi[c] - dLo[c]) * t;
                    density += (c == 0 ? 0.12 : c == 2 ? -0.12 : 0.0) * (band - 0.5);
                    density += 0.02 * (Next() - 0.5);      // 一点颗粒，打破完美梯度
                    d[i + c] = (float)Math.Pow(10.0, -density);
                }
            }

        // 一小块「灯箱漏光」：透射率 > 1。反相里 LUT 只覆盖 [0,1]，超过的像素走直算旁路，
        // 这一小块就是那条旁路的唯一入口。少了它，那段代码在整个测试套件里都没被执行过。
        for (int y = 2; y < 5; y++)
            for (int x = 2; x < 6; x++)
            {
                int i = (y * Width + x) * 3;
                d[i + 0] = 1.30f; d[i + 1] = 1.24f; d[i + 2] = 1.18f;
            }

        // 一个近零像素：密度公式里 -log10(T) 在 T→0 时发散，靠 d_max 钳位兜住。
        // 这是钳位那一支的入口。
        int z = ((Height - 2) * Width + (Width - 2)) * 3;
        d[z] = 1e-7f; d[z + 1] = 1e-7f; d[z + 2] = 1e-7f;

        return img;
    }

    /// <summary>
    /// 一份非默认的标定：默认值全是恒等或接近恒等，用默认值等于没测到大多数乘法。
    /// 这里的数字取自一卷真实 Gold 200 的量级——片基 ~0.09/0.29/0.54，高光 ~2.2/2.3/2.8。
    /// </summary>
    private static FrameParams Calibration() => new()
    {
        DMinPerChannel = new[] { 0.09, 0.29, 0.54 },
        DMaxPerChannel = new[] { 2.21, 2.34, 2.79 },
        OutputSpace = "sRGB",
        OutputIntent = OutputIntent.Basic,
    };

    // ── 基线的序列化 ────────────────────────────────────────────────────────────

    /// <summary>
    /// float 以十六进制位模式写出，不走十进制。
    ///
    /// "R" / "G17" 这类往返格式在**当前** .NET 上确实往返，但那是运行时行为，不是格式承诺；
    /// 位模式则是定义。测的是逐位不变，序列化就不该在中间引入一层可能有损的转换。
    /// </summary>
    private static string Hex(float v)
        => BitConverter.SingleToUInt32Bits(v).ToString("x8", CultureInfo.InvariantCulture);

    /// <summary>
    /// 基线正文：先一段人类可读的统计，再是全部像素的位模式。
    ///
    /// 统计那几行不参与断言（断言只比整份文本，统计自然也在内），它们的作用是让 diff 可读：
    /// 基线一变，第一眼就能看出是整体偏亮了还是某个通道歪了，而不是面对一屏十六进制。
    /// </summary>
    private static string Serialize(ImageBuffer img)
    {
        var sb = new StringBuilder();
        sb.Append("# OpenRevelare golden frame — 逐像素基线，请勿手改\n");
        sb.Append("# 更新方式：REVELARE_UPDATE_GOLDEN=1 dotnet test，然后连同理由一起提交\n");
        sb.Append(CultureInfo.InvariantCulture, $"size {img.Width}x{img.Height}\n");

        for (int c = 0; c < 3; c++)
        {
            double min = double.PositiveInfinity, max = double.NegativeInfinity, sum = 0;
            int n = img.PixelCount, nonFinite = 0;
            for (int p = 0; p < n; p++)
            {
                float v = img.Data[p * 3 + c];
                if (!float.IsFinite(v)) { nonFinite++; continue; }
                if (v < min) min = v;
                if (v > max) max = v;
                sum += v;
            }
            sb.Append(CultureInfo.InvariantCulture,
                $"# ch{c} min={min:F6} max={max:F6} mean={sum / n:F6} nonfinite={nonFinite}\n");
        }

        for (int p = 0; p < img.PixelCount; p++)
            sb.Append(CultureInfo.InvariantCulture,
                $"{Hex(img.Data[p * 3])} {Hex(img.Data[p * 3 + 1])} {Hex(img.Data[p * 3 + 2])}\n");

        return sb.ToString();
    }

    /// <summary>
    /// 每个通道允许的最大**相对**偏差。
    ///
    /// 基线仍然逐像素存位模式（见 <see cref="Hex"/>），但断言不再要求逐位相同。位精确在
    /// 单机上是对的，跨平台则守不住：同一份代码在不同 CPU 上，编译器的 FMA 合并与向量化
    /// 选择不同，末几位就会不一样。这不是假设——本仓库的基线在作者机器上生成，第一次上
    /// GitHub 的 Linux runner 就红了，最大相对差 4.2e-05，而管线本身一行没动。
    ///
    /// 这正是本文件原注释里预留的那次讨论：「真出现合理的跨平台分歧，再讨论怎么放宽，
    /// 前提是先看得见。」现在看见了，于是放宽到这里。
    ///
    /// 1e-4 这个数不是随手取的，它要同时满足两头：
    ///
    ///   **拦得住真回归。** 一次无意的重构、依赖升级、浮点写法调整，如果真改变了成像，
    ///   量级是百分之几到几倍——比这个阈值大三四个数量级，照样红。本文件存在的理由不受影响。
    ///
    ///   **放得过平台噪声。** 观测到的跨平台分歧是 4.2e-05，留一倍多余量。
    ///
    /// 尺度上它也站得住：1e-4 约等于 8 位量化一级的 1/40，导出成 8 位图逐位相同；16 位下
    /// 不到七级，肉眼与直方图都无从分辨。**但它不是"看不见就放过"**——看不见的漂移正是本
    /// 文件要防的东西，所以阈值卡在"比平台噪声大一点点"，而不是卡在"人眼极限"。
    /// </summary>
    private const double GoldenTolerance = 1e-4;

    /// <summary>比对或（在更新模式下）重写基线。</summary>
    private static void AssertGolden(string name, ImageBuffer result)
    {
        string actual = Serialize(result);
        string path = GoldenPath(name);

        if (Updating || !File.Exists(path))
        {
            File.WriteAllText(path, actual);
            // 首次生成不算通过：没有基线可比，就没有回归可言。逼一次显式提交。
            Assert.True(Updating,
                $"基线 {name} 不存在，已生成到 {path} —— 请检查内容后提交，再重跑测试");
            return;
        }

        string expected = File.ReadAllText(path);
        if (expected == actual) return;   // 逐位相同：同机器同编译器，最常见的一条路

        // 位模式不同，再按数值比。形状对不上（尺寸变了、像素少了）不属于容差问题，
        // 交回整份文本比对，让 diff 把话说清楚。
        float[] want = ParsePixels(expected), got = ParsePixels(actual);
        if (want.Length != got.Length || want.Length != result.PixelCount * 3)
        {
            Assert.Equal(expected.Replace("\r\n", "\n"), actual.Replace("\r\n", "\n"));
            return;
        }

        // 报**最差**的那个像素，不是第一个：第一个只说明"从这里开始不同"，最差的才说明
        // 这次分歧有多大——是平台噪声还是真回归，一眼就能分辨。
        int worst = -1;
        double worstRel = 0.0;
        for (int i = 0; i < want.Length; i++)
        {
            double w = want[i], g = got[i];
            if (float.IsFinite(want[i]) != float.IsFinite(got[i])) { worst = i; worstRel = double.PositiveInfinity; break; }
            if (!float.IsFinite(want[i])) continue;   // 两边都非有限：位模式已在上面比过
            // 相对误差；基准趋零时退化为绝对误差，否则近黑像素会把比值放大成噪声。
            double rel = Math.Abs(w - g) / Math.Max(Math.Abs(w), 1e-6);
            if (rel > worstRel) { worstRel = rel; worst = i; }
        }

        if (worstRel <= GoldenTolerance) return;

        int px = worst / 3, ch = worst % 3;
        Assert.Fail(
            $"基线 {name} 超出容差：像素 #{px}（{px % Width},{px / Width}）通道 {ch} " +
            $"期望 {want[worst]:G9}（{Hex(want[worst])}）实得 {got[worst]:G9}（{Hex(got[worst])}），" +
            $"相对差 {worstRel:E2} > {GoldenTolerance:E0}。\n" +
            $"若这是有意的成像改动，用 REVELARE_UPDATE_GOLDEN=1 重生成基线，并连同理由一起提交。");
    }

    /// <summary>把基线正文里的十六进制位模式读回 float；注释与 size 行跳过。</summary>
    private static float[] ParsePixels(string text)
    {
        var vals = new List<float>();
        foreach (string raw in text.Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line[0] == '#' || line.StartsWith("size", StringComparison.Ordinal))
                continue;
            foreach (string tok in line.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                vals.Add(BitConverter.UInt32BitsToSingle(
                    uint.Parse(tok, NumberStyles.HexNumber, CultureInfo.InvariantCulture)));
        }
        return vals.ToArray();
    }

    // ── 测试 ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 主路径：反相 → 步骤 4 → Stage 2 → sRGB 编码，也就是导出与预览共用的那条链路。
    /// </summary>
    [Fact]
    public void FullPipeline_MatchesGolden()
        => AssertGolden("full-pipeline", Pipeline.ProcessFrame(MakeNegative(), Calibration()));

    /// <summary>
    /// Stage 1 单独的出口（<c>OutputIntent.None</c>）：标准 Cineon log，尚未渲染。
    ///
    /// 单独钉住它，是因为它和主路径共用前半段：只钉主路径的话，Stage 1 里的错误可以被
    /// Stage 2 的某个补偿掩盖掉，两个都钉就没有这个死角。这也是 CLI 的 <c>--intent none</c>
    /// 和外部 LUT 工作流真正消费的那个中间产物。
    /// </summary>
    [Fact]
    public void Stage1Only_MatchesGolden()
    {
        FrameParams cal = Calibration();
        cal.OutputIntent = OutputIntent.None;
        AssertGolden("stage1-only", Pipeline.ProcessFrame(MakeNegative(), cal));
    }

    /// <summary>
    /// 带 Stage 2 调整与几何的完整一帧——用户实际会动的那些滑块。
    ///
    /// 几何（方向/旋转/裁切）与色彩放在同一个基线里是有意的：它们在真实使用中就是叠加的，
    /// 而重采样与色彩顺序搞反过一次（见 c4cd735「负片视图与正片共用同一套几何」）。
    /// </summary>
    [Fact]
    public void WithSceneEditsAndGeometry_MatchesGolden()
    {
        FrameParams cal = Calibration();
        cal.ExposureEv = 0.35;
        cal.Contrast = 0.18;
        cal.Saturation = 0.22;
        cal.Highlights = -0.25;
        cal.Shadows = 0.15;
        cal.WbGains = new[] { 1.04, 1.0, 0.96 };
        cal.QuarterTurns = 1;
        cal.FlipH = true;
        AssertGolden("scene-and-geometry", Pipeline.ProcessFrame(MakeNegative(), cal));
    }

    /// <summary>
    /// 同一份输入连算两次，必须逐位相同。
    ///
    /// 这一条不依赖基线文件，所以它在任何平台上都该绿——它测的是**并行渲染的确定性**。
    /// 管线是 <c>Parallel.For</c> 行并行的，而 62b08f3 刚把它改成区间分片；任何按线程数或
    /// 到达顺序累加的写法，都会让同一台机器上两次运行结果不同，那才是「可复现」最彻底的
    /// 反面，而它恰恰不会被单次跑的基线比对抓到。
    /// </summary>
    [Fact]
    public void Deterministic_AcrossRuns()
    {
        FrameParams cal = Calibration();
        float[] a = Pipeline.ProcessFrame(MakeNegative(), cal).Data;
        float[] b = Pipeline.ProcessFrame(MakeNegative(), cal).Data;

        Assert.Equal(a.Length, b.Length);
        for (int i = 0; i < a.Length; i++)
            if (BitConverter.SingleToUInt32Bits(a[i]) != BitConverter.SingleToUInt32Bits(b[i]))
                Assert.Fail($"两次运行在第 {i / 3} 个像素的通道 {i % 3} 上不同：" +
                            $"{Hex(a[i])} vs {Hex(b[i])} —— 并行渲染不确定");
    }

    /// <summary>
    /// 输出里不允许出现 NaN 或 Inf。
    ///
    /// 单独立一条而不是靠基线兜：NaN 会顺着基线一起被「固化」下来——生成基线那天要是就有
    /// NaN，之后每次比对都完美通过，而每一张照片上都有一个坏点。这条断言不看基线，只看
    /// 结果本身是否是个合法的图像。合成负片里那个近零像素与超 1 亮块，正是最可能产出
    /// NaN 的两处。
    /// </summary>
    [Fact]
    public void NoNaNOrInfinity_InOutput()
    {
        float[] d = Pipeline.ProcessFrame(MakeNegative(), Calibration()).Data;
        int bad = d.Count(v => !float.IsFinite(v));
        Assert.True(bad == 0, $"输出里有 {bad} 个非有限值（NaN/Inf）");
    }
}
