using Avalonia.Controls;
using Avalonia.Layout;
using OpenRevelare.Gui.Services;

namespace OpenRevelare.Gui.Views;

internal sealed class PureNegTrainingFeedbackDialog : Window
{
    public PureNegTrainingFeedbackDialog(string? savedPath = null)
    {
        Title = Loc.T(savedPath is null ? "帮助改进 PURE Neg" : "PURE Neg 训练数据包已保存");
        Width = 620; Height = 510; MinWidth = 380; MinHeight = 320;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var body = new StackPanel { Spacing = 12 };
        body.Children.Add(new TextBlock { Text = Title, Classes = { "dialogTitle" } });
        body.Children.Add(Note("将 PURE Neg 除尘检测和你的修正结果分享给原作者，帮助继续训练和改进识别。分享完全自愿，不会自动上传。"));
        if (savedPath is not null)
        {
            body.Children.Add(new SelectableTextBlock { Text = savedPath, TextWrapping = Avalonia.Media.TextWrapping.Wrap });
            var folder = new Button { Content = Loc.T("打开数据包文件夹") };
            folder.Click += (_, _) => Updater.OpenUrl(savedPath);
            body.Children.Add(folder);
        }
        body.Children.Add(Note("请检查完整图像（包括当前裁切外区域），仅分享你有权提供的照片。将整个数据包文件夹压缩为 ZIP，说明漏检或误修位置及你的修正；附件过大时先联系作者。"));
        body.Children.Add(Note("PURE Neg 作者反馈邮箱"));
        body.Children.Add(new SelectableTextBlock { Text = PureNegFeedback.Email, FontSize = 16 });
        body.Children.Add(Note("数据包内附有分享说明。是否采纳样本及如何训练，由 PURE Neg 原项目决定。"));
        var official = new Button { Content = Loc.T("查看官方反馈说明") };
        official.Click += (_, _) => Updater.OpenUrl(PureNegFeedback.InstructionsUrl);
        body.Children.Add(official);
        var email = new Button { Content = Loc.T("写邮件给 PURE Neg 作者") };
        email.Click += (_, _) => Updater.OpenUrl(PureNegFeedback.MailUrl);
        var copy = new Button { Content = Loc.T("复制反馈邮箱") };
        copy.Click += async (_, _) => { if (Clipboard is { } clipboard) await clipboard.SetTextAsync(PureNegFeedback.Email); };
        var close = new Button { Content = Loc.T("关闭"), IsCancel = true };
        close.Click += (_, _) => Close();
        Content = DialogLayout.Compose(body, DialogLayout.Actions(copy, email, close));
    }

    private static TextBlock Note(string text) => new() { Text = Loc.T(text), TextWrapping = Avalonia.Media.TextWrapping.Wrap };
}
