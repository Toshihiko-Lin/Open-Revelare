using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using OpenRevelare.Gui.Services;

namespace OpenRevelare.Gui.Views;

internal sealed class DustTrainingExportDialog : Window
{
    private readonly List<Bitmap> _thumbnails = [];
    public bool ClearCache { get; private set; }

    public DustTrainingExportDialog(IReadOnlyList<DustTrainingSample> samples)
    {
        Title = Loc.T("导出 PURE Neg 训练数据包");
        Width = 820; Height = 620; MinWidth = 400; MinHeight = 320;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var body = new StackPanel { Spacing = 12 };
        body.Children.Add(new TextBlock { Text = Title, Classes = { "dialogTitle" } });
        body.Children.Add(new TextBlock
        {
            Text = Loc.T("这些样本来自 PURE Neg 除尘检测和你的人工修正，可自愿分享给原项目继续训练，帮助减少漏检和误修。"),
            TextWrapping = TextWrapping.Wrap,
        });
        body.Children.Add(new TextBlock { Text = Loc.T("PURE Neg 作者反馈邮箱"), TextWrapping = TextWrapping.Wrap });
        body.Children.Add(new SelectableTextBlock { Text = PureNegFeedback.Email });
        body.Children.Add(new TextBlock
        {
            Text = Loc.T("默认不选择任何照片。缩略图显示完整检测画面；导出的数据包包含该完整图像，包括当前裁切外的区域。仅保存到本地，由你自行分享。"),
            TextWrapping = TextWrapping.Wrap,
        });
        body.Children.Add(new TextBlock
        {
            Text = Loc.T("已应用的除尘样本自动保留在本机，重启后仍可导出。当前审核也可导出，取消的审核不保留；可用「清除已保留样本」删除本机样本，不影响照片修复。"),
            TextWrapping = TextWrapping.Wrap, Classes = { "dialogNote" },
        });
        var cards = new WrapPanel();
        body.Children.Add(cards);
        var selections = new List<(DustTrainingSample Sample, CheckBox Select, CheckBox Clean, CheckBox Test)>();
        var export = new Button { Classes = { "primary" }, IsEnabled = false };
        void Update()
        {
            int count = selections.Count(s => s.Select.IsChecked == true);
            export.Content = Loc.F($"导出所选 {count} 张…");
            export.IsEnabled = count > 0;
        }
        foreach (var sample in samples)
        {
            var card = new StackPanel { Spacing = 6, Width = 228, Margin = new Thickness(8) };
            var select = new CheckBox { Content = sample.Label, IsChecked = false };
            ToolTip.SetTip(select, sample.Label);
            card.Children.Add(select);
            var image = new Image { Width = 216, Height = 162, Stretch = Stretch.Uniform };
            if (sample.Input.ThumbnailPng is { } png)
            {
                using var stream = new MemoryStream(png);
                var bitmap = new Bitmap(stream);
                _thumbnails.Add(bitmap);
                image.Source = bitmap;
            }
            card.Children.Add(image);
            card.Children.Add(new TextBlock { Text = $"{sample.Input.Width} × {sample.Input.Height}", FontSize = 12 });
            var clean = new CheckBox { Content = Loc.T("整张已检查并修干净"), IsChecked = false, IsEnabled = false };
            var test = new CheckBox { Content = Loc.T("建议作为评估样本（test）"), IsChecked = false, IsEnabled = false };
            card.Children.Add(clean); card.Children.Add(test);
            selections.Add((sample, select, clean, test));
            select.IsCheckedChanged += (_, _) =>
            {
                clean.IsEnabled = test.IsEnabled = select.IsChecked == true;
                Update();
            };
            cards.Children.Add(new Border
            {
                BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6), Margin = new Thickness(4), Child = card,
            });
        }
        var none = new Button { Content = Loc.T("全不选") };
        none.Click += (_, _) => { foreach (var entry in selections) entry.Select.IsChecked = false; };
        var clear = new Button { Content = Loc.T("清除已保留样本") };
        clear.Click += (_, _) => { ClearCache = true; Close(); };
        var cancel = new Button { Content = Loc.T("取消"), IsCancel = true };
        cancel.Click += (_, _) => Close();
        export.Click += (_, _) => Close(selections.Where(s => s.Select.IsChecked == true)
            .Select(s => new DustTrainingBatch.Choice(s.Sample,
                new(s.Clean.IsChecked == true, s.Test.IsChecked == true), Selected: true)).ToArray());
        Content = DialogLayout.Compose(body, DialogLayout.Actions(none, clear, cancel, export));
        Update();
        Closed += (_, _) => { foreach (var thumbnail in _thumbnails) thumbnail.Dispose(); };
    }
}
