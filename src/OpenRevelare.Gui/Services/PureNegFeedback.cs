namespace OpenRevelare.Gui.Services;

internal static class PureNegFeedback
{
    public const string Email = "easonzhang@studiopure.cn";
    public const string InstructionsUrl = "https://github.com/easonzhangbin/PURENeg#反馈识别问题--report-detection-errors";
    public static string MailUrl => "mailto:" + Email + "?subject=" +
        Uri.EscapeDataString("PURE Neg training samples from OpenRevelare " + AppInfo.Version);

    public const string Readme = """
        # PURE Neg 训练数据包 / Training contribution

        这是用户在 OpenRevelare 中使用 PURE Neg 自动除尘并检查、修正结果后，主动选择导出的训练样本。
        可自愿分享给 PURE Neg 原项目，帮助作者改进灰尘、纤维和划痕检测。不会自动上传，也不会在本机启动模型训练。
        These samples come from PURE Neg dust detection and the user's review/corrections in OpenRevelare.
        Sharing is optional, to help the upstream author improve detection. No automatic upload or local model training occurs.

        每个 *-training 子目录包含完整检测画面 source.tiff（包括当前裁切外区域）、repair-preview.tiff、自动/最终/补修/擦除蒙版和 pack.json；有画笔记录或概率图时一并保留。
        source.tiff 是送入检测引擎的画面，不是相机 RAW；repair-preview.tiff 是检测空间的修复参考，不是最终色彩管理导出。
        Each sample contains the full detector image (including areas outside the current crop), repair preview, masks and metadata, plus stroke history/probability maps when available.
        The source is the detector input, not camera RAW. The repair preview is not the final colour-managed photo export.

        反馈步骤 / How to share
        1. 检查全部图像，只分享你有权提供且愿意公开给作者的内容。
        2. 将整个 training-dataset-* 文件夹压缩为 ZIP，保留所有子目录及文件。
        3. 说明 OpenRevelare 版本、系统、检测设置、漏检或误修位置，以及你做了哪些修正；若方便，可另附有权分享的原始扫描件。
        4. 手动发邮件至 easonzhang@studiopure.cn。附件过大时先邮件联系作者商定传输方式。
        Review the full images and sharing rights, ZIP the entire dataset folder, describe missed/false detections and corrections, and email it manually. Contact the author first for large attachments.

        只有确认整张修干净时才标记 clean；未标记像素不代表已经人工确认干净。test 表示建议作为评估样本，由原项目决定是否采纳及如何训练。
        Mark clean only after checking the whole image. Unmarked pixels are not certified clean. The upstream author decides whether and how to use contributions.

        官方反馈说明 / Official feedback instructions:
        https://github.com/easonzhangbin/PURENeg#反馈识别问题--report-detection-errors
        原项目 / Upstream: https://github.com/easonzhangbin/PURENeg
        """;
}
