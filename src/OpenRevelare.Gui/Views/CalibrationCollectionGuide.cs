using Avalonia.Controls;
using Avalonia.Media;
using OpenRevelare.Gui.Services;

namespace OpenRevelare.Gui.Views;

internal sealed class CalibrationCollectionGuide : Window
{
    public CalibrationCollectionGuide(bool camera)
    {
        Title = Loc.T(camera ? "相机校准素材采集" : "扫描仪校准素材采集");
        Classes.Add("dialog"); Width = 720; Height = 640; MinWidth = 420; MinHeight = 350;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var body = new StackPanel { Spacing = 16 };
        void Step(string heading, string text)
        {
            body.Children.Add(new TextBlock { Text = Loc.T(heading), Classes = { "dialogTitle" } });
            body.Children.Add(new TextBlock { Text = Loc.T(text), TextWrapping = TextWrapping.Wrap });
        }
        if (camera)
        {
            Step("1. 架好相机，先不要放胶片", "把相机固定在翻拍架或三脚架上，镜头正对灯板并与片架平行。打开灯板，保持亮度档位不变。调整构图，使准备扫描的区域被均匀照亮，遮住片架周围会漏进镜头的强光。");
            Step("2. 设好相机", "选择 RAW 和手动曝光 M 档，关闭自动 ISO；使用相机正常低 ISO，固定光圈。用一张胶片放大对焦，切换手动对焦并固定位置，再移开胶片。关闭可关闭的机内自动亮度和长曝光降噪，记录不能关闭的处理。不要在后续三类照片之间改设置。");
            Step("3. 确定快门，拍无片照片", "取出胶片，保留片架和原光路。试拍并检查各通道高光，缩短快门直到无片照片不过曝，再留出余量；相机直方图通常来自预览，不能当作 RAW 饱和的精确证明。设置确定后不再改 ISO、光圈、快门或灯板亮度。用遥控或自拍定时拍摄，保留原 RAW，这张登记为无片场。");
            Step("4. 盖上镜头盖，拍暗场", "保持相机电源、ISO、光圈、快门和机位不变，盖严镜头盖并防止其他漏光，拍一张 RAW。不要把快门改得更快，也不要关机。这张登记为暗场。拍完取下镜头盖，注意不要碰动对焦环或机位。");
            Step("5. 放回胶片，拍内容照片", "把胶片放回同一片架位置，用刚才固定的设置拍摄。不要因为负片看起来暗就改曝光。需要换设置时，为新设置重新拍无片场和暗场，另建配置。未曝光的橙色片边属于胶片片基，不是无片场。");
            Step("6. 拷贝原文件并试算", "直接从存储卡拷贝 RAW，不先经过 Lightroom、NX Studio 等软件调色或转 TIFF。选择相机、传感器原始采样、设备通道相对透射率；分别登记暗场和无片场，再选择内容 RAW 试算。首版每类使用一张参考，不会自动平均多张。试算不修改原片或胶卷。");
            Step("7. 需要更可靠的测量时", "可以在不动设备和设置的情况下再拍一组独立参考及同片照片，用于检查稳定性。要验证绝对测量误差，还需要有实测数据的透射目标；不要求先把胶片冲洗成标准状态。不要只凭反转后看起来好看就判定采集已校准。");
        }
        else
        {
            Step("1. 优先选择已有标准流程", "已有 SilverFast、VueScan 或 ArgyllCMS 校准产物时直接导入，不必填写自定义 JSON。记录扫描仪型号、软件版本和透射/反射模式；同型号别人的配置不能代替本机验证。厂家白校准、对焦等维护按该型号手册执行。");
            Step("2. 找到色靶和它对应的数据", "胶片使用能放入片架的透射 IT8.7/1 或 Q60 色靶，纸稿使用反射 IT8.7/2，分别建立配置。按色靶或保护套上的批次、条码或序列号，到供应商取得匹配的参考文件。支持 IT8/CGATS 文本、Q60/CIE 数据及 Argyll CTI3 单表；扩展名不是判断依据。");
            Step("3. SilverFast 用户", "按所安装版本的 IT8 Calibration 流程放置色靶、预览并对齐色块，选择或自动取得该靶参考文件，生成并导出输入 ICC。将 ICC 和原始参考数据导入本软件，并记录透射/反射模式。不要把已经使用该 ICC 转换过的扫描再重复套用它。");
            Step("4. VueScan 用户", "支持校准的版本中，选择 Input / Task 的 Profile scanner，指定 Color / Scanner IT8 data 和 Scanner ICC profile，预览并对齐色靶，再执行 Profile / Profile scanner。保留生成的 scanner.icc 或自定名称 ICC 及对应参考文件，导入本软件。菜单以安装版本为准，部分功能需要 Professional Edition。");
            Step("5. ArgyllCMS 用户", "保留与实际色靶布局匹配的 .cht 和厂商参考数据，使用 scanin 从未调色的目标扫描提取测量，再用 colprof 生成输入 ICC。导入 .ti3 可识别设备 RGB 与 XYZ/Lab/光谱字段，导入 ICC 可核验输入设备类型；本软件不会自动安装或运行外部工具。");
            Step("6. 明确导入后的作用", "标准文件入口会识别参考表、测量字段及输入 ICC，登记摘要和来源。Lab/XYZ 是色度参考，不会被转换成标准密度；输入 ICC 也不代表物理透射测量或负片反转已完成。保存配置不等于已经应用到胶卷。");
            Step("7. 专用物理密度测量（高级）", "需要透射阶梯的实测密度响应时，再展开高级入口。那里使用独立训练/验证扫描及带 ROI 的密度模板；普通 IT8 的 Lab/XYZ 不能直接填入该模板。参考定义、测量条件和材料范围必须来自实际参考，不从文件扩展名或扫描仪型号猜测。");
        }
        Step("8. 在新建卷中接入流程", "保存设备配置后，在新建卷的采集校准中选择它。已在上游校准的图像选择仅记录；要由本软件处理时，明确选择相机暗场／无片场校正或扫描仪输入 ICC。应用时会生成原片旁的浮点 TIFF 工作副本，随后统一用于预览、自动反转和导出。请保留原片及参考素材。");
        var close = new Button { Content = Loc.T("关闭"), IsCancel = true }; close.Click += (_, _) => Close();
        Content = DialogLayout.Compose(body, DialogLayout.Actions(close));
    }
}
