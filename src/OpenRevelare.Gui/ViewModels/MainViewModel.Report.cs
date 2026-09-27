using System.Text;
using OpenRevelare.Core;
using OpenRevelare.Gui.Services;

namespace OpenRevelare.Gui.ViewModels;

/// <summary>
/// The per-frame technical report: every number this frame's rendering rests on, and — the part
/// that matters — WHERE each one came from.
///
/// WHY IT EXISTS. The program's claim is that the reconstruction is checkable, not merely
/// explainable. Until now the facts backing that claim were spread across three places and two of
/// them were invisible: the film base reads off a slider without saying whether it was measured on
/// this frame or inherited from another, and the input domain — what the file's numbers MEAN, which
/// decides everything downstream — was settled at import and then never shown at all. A cast that
/// traces back to a TIFF assumed to be sRGB when it was linear is unfindable from the sliders, and
/// perfectly obvious from one line of this.
///
/// It is a REPORT, not a control: nothing here is editable, and every line is either a measurement,
/// a parameter or the provenance of one. That is also why it lives under 帮助 rather than in the
/// panel — it is consulted when something looks wrong, not while grading.
/// </summary>
public sealed partial class MainViewModel
{
    /// <summary>
    /// The report for the current frame, as plain text. Never throws: it is what the user reaches
    /// for when something is already wrong, so a missing piece is reported as a missing piece.
    /// </summary>
    public string BuildFrameReport()
    {
        var sb = new StringBuilder();
        try
        {
            AppendFrameIdentity(sb);
            AppendInputDomain(sb);
            AppendOpticalCalibration(sb);
            AppendFilmBase(sb);
            AppendEndpoints(sb);
            AppendOutput(sb);
        }
        catch (Exception ex)
        {
            sb.AppendLine();
            sb.AppendLine(Loc.F($"（报告生成中断：{ex.Message}）"));
        }
        return sb.ToString();
    }

    /// <summary>
    /// The domain immediately upstream of film-base and endpoint measurement. LCC and Path A
    /// change those numbers before inversion, so omitting them made an otherwise detailed report
    /// impossible to reproduce: the same RAW plus different flat/separation matrices is not the
    /// same measurement.
    /// </summary>
    private void AppendOpticalCalibration(StringBuilder sb)
    {
        sb.AppendLine();
        sb.AppendLine(Loc.T("【光学与分光校准】"));

        if (LccEnabled && _lccFlatField is { } flat)
        {
            string source = string.IsNullOrWhiteSpace(_lccSourcePath)
                ? Loc.T("（来源路径未记录）")
                : _lccSourcePath;
            sb.AppendLine(Loc.F($"  LCC　　 开启 · {flat.Width}×{flat.Height} 平场 · {source}"));
        }
        else
        {
            sb.AppendLine(Loc.T("  LCC　　 关闭"));
        }

        if (_decoupleMatrix is { } decouple)
        {
            string source = string.IsNullOrWhiteSpace(_calSourceDir)
                ? Loc.T("（校正图路径未记录）")
                : _calSourceDir;
            sb.AppendLine(Loc.F($"  Path A　 开启（线性 RGB 分光）· {source}"));
            AppendMatrix(sb, Loc.T("  解耦矩阵"), decouple);
            if (_decoupleChromaMatrix is { } chroma)
            {
                double[] amplification = DecoupleCalibration.ChromaAxisAmplificationFromCompensationMatrix(chroma);
                sb.AppendLine(Loc.F($"  轴向放大　YB ×{amplification[0]:F3} · RG ×{amplification[1]:F3}（色度反压抵消）"));
                AppendMatrix(sb, Loc.T("  色度反压"), chroma);
            }
            else
                sb.AppendLine(Loc.T("  色度反压　无（旧工程或未完成标定）"));
        }
        else
        {
            sb.AppendLine(Loc.T("  Path A　 关闭（宽谱 / 扫描仪输入）"));
        }

        sb.AppendLine(Loc.T("  次序　　 镜头畸变 → LCC → 暗角 → Path A 解耦 → 片基 / 高光测量 → 反相"));
    }

    private static void AppendMatrix(StringBuilder sb, string label, double[,] matrix)
    {
        if (matrix.GetLength(0) < 3 || matrix.GetLength(1) < 3)
        {
            sb.AppendLine(label + Loc.T("　格式无效"));
            return;
        }
        for (int r = 0; r < 3; r++)
            sb.AppendLine(label + (r == 0 ? "　" : "　　　　 ")
                + $"[{matrix[r, 0],8:F5} {matrix[r, 1],8:F5} {matrix[r, 2],8:F5}]");
    }

    private void AppendFrameIdentity(StringBuilder sb)
    {
        sb.AppendLine(Loc.T("【画面】"));
        if (CurrentFrame is not { } frame)
        {
            sb.AppendLine(Loc.T("  没有打开的画面"));
            return;
        }

        sb.AppendLine(Loc.F($"  文件　　{frame.Path}"));
        string roll = CurrentRoll?.Title ?? Loc.T("（未登记）");
        sb.AppendLine(Loc.F($"  卷　　　{roll}") + (frame.IsVirtual ? Loc.T("　·　虚拟副本") : ""));
        if (frame.Params.SplitCell is { } cell)
        {
            sb.AppendLine(Loc.F(
                $"  分格　　同一扫描文件的第 {cell.X:F3},{cell.Y:F3} 处（{cell.W:F3}×{cell.H:F3}，按整图归一）"));
        }
    }

    /// <summary>
    /// What the file's numbers mean. This is the one the user cannot see anywhere else, and the one
    /// a wrong answer in is indistinguishable from a bad negative.
    /// </summary>
    private void AppendInputDomain(StringBuilder sb)
    {
        sb.AppendLine();
        sb.AppendLine(Loc.T("【输入域】"));
        if (CurrentFrame is not { } frame) { sb.AppendLine(Loc.T("  —")); return; }

        string path = frame.Path;
        if (RawDecode.IsRawExtension(path))
        {
            sb.AppendLine(Loc.F($"  路径　　相机 RAW（LibRaw，UniWB 线性解码；后端 {Settings.Current.DecodeBackend}）"));
            double[,]? matrix = RawDecode.CameraToSrgbMatrix(path, out string diagnosis);
            sb.AppendLine(matrix is not null
                ? Loc.T("  相机矩阵　数据库可用但未应用：负片翻拍保持相机原生通道，等待灯光 / 胶片联合表征")
                : Loc.F($"  相机矩阵　无 —— {diagnosis}；输入原色按约定处理，色彩为推定而非表征"));
            if (RawDecode.ReadNefCompression(path) is { } nef)
                sb.AppendLine(Loc.F($"  NEF 压缩　{nef}"));
        }
        else
        {
            sb.AppendLine(Loc.T("  路径　　扫描件（TIFF / JPEG / PNG / FFF）"));
            sb.AppendLine(Loc.F($"  解释　　{DescribeTiffInput()}"));
            sb.AppendLine(_tiffInputAssumption != TiffInputAssumption.Unspecified
                ? Loc.T("  依据　　用户在卷上明确指定，不再自动判断")
                : _tiffInputDetection is { IsConclusive: true }
                    ? Loc.T("  依据　　读自文件自身的声明（可信）")
                    : Loc.T("  依据　　文件未声明，按约定推定（存疑：结果不对时先改这一项）"));
        }
        // TIFF carries the same decoder-side source lattice as RAW (including the conservative
        // linear-shadow equivalent after a TRC/ICC transform). Keeping this outside the RAW
        // branch makes the report agree with the endpoint risk flag instead of hiding the one
        // fact that explains why an 8-bit scan's dense blue channel is unstable.
        if (_previewLinear is { SourceQuantisationStep: > 0 } preview)
            sb.AppendLine(Loc.F($"  源量化　约 {1.0 / preview.SourceQuantisationStep:F0} 级（步长 {preview.SourceQuantisationStep:G4}）"));
        sb.AppendLine(Loc.F($"  管线　　{ColorPipelineDiagnostic}"));
    }

    private void AppendFilmBase(StringBuilder sb)
    {
        sb.AppendLine();
        sb.AppendLine(Loc.T("【片基】"));
        // TBase is deliberately neutral in the live endpoint model; the physical carrier moved
        // into the absolute black endpoint. Reconstructing transmission from D_min is the exact
        // inverse migration, so the report shows the measurable film fact instead of the internal
        // 1/1/1 reference sentinel (which previously made every calibrated roll look unsampled).
        double[] dmin = DMinPerChannel;
        double[] tb = TBaseFromDensity(dmin);
        sb.AppendLine(Loc.F($"  T_base　R {tb[0]:F4} · G {tb[1]:F4} · B {tb[2]:F4}"));
        // The orange mask is a physical fact with a direction: a colour negative's base passes
        // red most and blue least. A reading that does not is a reading taken off something that
        // is not the base — the board, a fogged frame, the surround — and saying so here is
        // cheaper than the hour it otherwise costs.
        //
        // ON A BLACK-AND-WHITE ROLL THE RULE DOES NOT APPLY AND MUST NOT BE PRINTED. A silver
        // image has no mask, so the three readings differ only by the sensor and the light, in
        // whatever order those happen to fall — and the check would then accuse a perfectly good
        // roll of having sampled the light board. What matters there is which reading is used, so
        // that is what it says instead.
        bool cameraNative = CurrentFrame is { } frame && RawDecode.IsRawExtension(frame.Path);
        sb.AppendLine(Monochrome
            ? Loc.F($"  形态　　黑白卷，不适用色罩次序；渲染只用 G 的读数 {tb[1]:F4}（三通道已折成一路）")
            : cameraNative
                ? Loc.T("  形态　　相机原生 RAW 通道不适用 R ≥ G ≥ B 校验；应以灯板众数 / 边缘拓扑证据判断片基")
            : tb[0] >= tb[1] && tb[1] >= tb[2]
                ? Loc.T("  形态　　R ≥ G ≥ B，与彩负橙色片基一致")
                : Loc.T("  形态　　不是 R ≥ G ≥ B —— 彩负片基不该是这个次序，请确认采样取到的是片基而非灯板或画面"));
        sb.AppendLine(dmin.All(v => Math.Abs(v) < 1e-9)
            ? Loc.T("  来源　　默认值 1/1/1（未采样：色罩由黑端承载，或这卷尚未标定）")
            : Loc.T("  来源　　本卷标定所得（采样或自动分析）"));
        if (!string.IsNullOrEmpty(FilmBaseText))
            sb.AppendLine(Loc.F($"  证据　　{FilmBaseText}"));
    }

    private void AppendEndpoints(StringBuilder sb)
    {
        sb.AppendLine();
        sb.AppendLine(Monochrome
            ? Loc.T("【反相的两个数（绝对密度）· 黑白】")
            : Loc.T("【反相的六个数（绝对密度）】"));
        double[] dmin = DMinPerChannel, dmax = DMaxPerChannel;
        if (Monochrome)
        {
            // The stored triples are whatever the roll last measured; the render collapses them
            // onto green. Printing all six here would show numbers the picture does not use.
            sb.AppendLine(Loc.F($"  D_min　 {dmin[1]:F3}　（黑端，片基一侧）"));
            sb.AppendLine(Loc.F($"  D_max　 {dmax[1]:F3}　（亮端；自动值为场景代理）"));
            sb.AppendLine(Loc.F($"  跨度　　{dmax[1] - dmin[1]:F3}　（两端之差＝反差；黑白卷没有通道间之差可言）"));
            if (dmax[1] - dmin[1] <= 0)
                sb.AppendLine(Loc.T("  ⚠ 白端不高于黑端，无法反相，请重新标定两端"));
            sb.AppendLine(Loc.T("  折叠　　三通道按 Rec.709 加权折成一路亮度后再进密度域"));
            if (!string.IsNullOrEmpty(HighlightConfidenceText))
                sb.AppendLine(Loc.F($"  证据　　{HighlightConfidenceText}"));
            return;
        }

        sb.AppendLine(Loc.F($"  D_min　 R {dmin[0]:F3} · G {dmin[1]:F3} · B {dmin[2]:F3}　（黑端，片基一侧）"));
        sb.AppendLine(Loc.F($"  D_max　 R {dmax[0]:F3} · G {dmax[1]:F3} · B {dmax[2]:F3}　（亮端；自动值为场景代理）"));

        double[] span = { dmax[0] - dmin[0], dmax[1] - dmin[1], dmax[2] - dmin[2] };
        sb.AppendLine(Loc.F($"  跨度　　R {span[0]:F3} · G {span[1]:F3} · B {span[2]:F3}　（两端之差＝反差；通道间之差＝色彩平衡）"));
        if (span.Any(v => v <= 0))
            sb.AppendLine(Loc.T("  ⚠ 有通道的白端不高于黑端，该通道无法反相，请重新标定两端"));
        if (!string.IsNullOrEmpty(HighlightConfidenceText))
            sb.AppendLine(Loc.F($"  证据　　{HighlightConfidenceText}"));
    }

    private void AppendOutput(StringBuilder sb)
    {
        sb.AppendLine();
        sb.AppendLine(Loc.T("【输出】"));
        sb.AppendLine(Loc.F($"  输出空间　{CurrentOutputSpace.Name}"));
        sb.AppendLine(HasPrintLut
            ? Loc.F($"  胶片风格　{PrintLutNames[PrintLutIndex]}（{PrintLutContractText}）")
            : Loc.T("  胶片风格　标准显示渲染（无印片 LUT）"));
        // A print stock carries its own cast, and on a black-and-white roll that is the ONE thing
        // that can put colour back into a picture the inversion made neutral. Said here because
        // "why is my black-and-white frame warm" has exactly one answer and this is it.
        if (Monochrome && HasPrintLut)
            sb.AppendLine(Loc.T("  注意　　黑白卷叠了印片 LUT：中性是反相的结果，印片自身的色偏会加在其上"));
        sb.AppendLine(ExportHdrLimitStops > 0d
            ? Loc.F($"  HDR　　　 已开，上限 +{ExportHdrLimitStops:0.0} 档")
            : Loc.T("  HDR　　　 关"));
    }
}
