using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using OpenRevelare.Gui.Services;

namespace OpenRevelare.Gui.Views;

internal sealed record BatchReportAction(bool Retry, string? LocateSource = null);

internal sealed class BatchExportReportDialog : Window
{
    private readonly ListBox _rows;
    private readonly ComboBox _filter;
    private readonly TextBox _details;
    private readonly TextBlock _visibleCount;
    private readonly Button _locate;
    private readonly BatchExportReport _report;

    internal BatchExportReportDialog(BatchExportJob job, Func<string, bool> canLocate)
    {
        Title = Loc.T("批量导出结果"); Width = 780; Height = 700; MinWidth = 480; MinHeight = 520;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        _report = new(job);
        var grid = new Grid { Margin = new Thickness(20), RowDefinitions = new("Auto,Auto,*,Auto,Auto") };
        var heading = new StackPanel { Spacing = 5 };
        heading.Children.Add(new TextBlock { Text = _report.Summary, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap });
        heading.Children.Add(new TextBlock { Text = Loc.F($"卷：{job.RollTitle} · 开始于 {job.StartedAt:yyyy-MM-dd HH:mm:ss}"), TextWrapping = TextWrapping.Wrap });
        heading.Children.Add(new TextBlock { Text = job.OptionsSummary, TextWrapping = TextWrapping.Wrap });
        string notice = string.Join("\n", new[] { job.SystemError, job.RecoveryBlockedReason,
            job.WasCancelled ? Loc.T("导出已取消，已完成的文件已保留") : "",
            Loc.T("重试会读取当前原片，沿用任务开始时的调整参数与输出路径。") }.Where(s => s.Length > 0));
        heading.Children.Add(new TextBox { Text = notice, IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MaxHeight = 80 });
        grid.Children.Add(heading);
        var filters = new WrapPanel { Margin = new Thickness(0, 10) };
        filters.Children.Add(new TextBlock { Text = Loc.T("显示："), VerticalAlignment = VerticalAlignment.Center });
        _filter = new ComboBox { Name = "ResultFilter", MinWidth = 140, ItemsSource = new[] {
            Loc.T("全部结果"), Loc.T("需要处理"), Loc.T("失败"), Loc.T("未执行"), Loc.T("完成"), Loc.T("跳过") }, SelectedIndex = 0 };
        _visibleCount = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
        filters.Children.Add(_filter); filters.Children.Add(_visibleCount); Grid.SetRow(filters, 1); grid.Children.Add(filters);
        var copy = new Button { Name = "CopyFullReport", Content = Loc.T("复制完整报告"), Margin = new Thickness(8, 0, 0, 0) };
        copy.Click += async (_, _) =>
        {
            try
            {
                if (Clipboard is null) throw new InvalidOperationException();
                await Clipboard.SetTextAsync(_report.FullText);
                _visibleCount.Text = Loc.T("报告已复制");
            }
            catch { _visibleCount.Text = Loc.T("无法复制报告"); }
        };
        filters.Children.Add(copy);
        _rows = new ListBox { Name = "ResultRows", ItemTemplate = new FuncDataTemplate<BatchExportReport.Row>((row, _) =>
            new TextBlock { Text = row?.Caption, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(4, 6) }) };
        Grid.SetRow(_rows, 2); grid.Children.Add(_rows);
        _details = new TextBox { Name = "ResultDetails", IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap,
            Height = 140, Margin = new Thickness(0, 10, 0, 0) };
        Grid.SetRow(_details, 3); grid.Children.Add(_details);
        var folder = new Button { Content = Loc.T("打开输出目录") };
        folder.Click += (_, _) =>
        {
            try
            {
                if (!Directory.Exists(job.Folder)) throw new DirectoryNotFoundException(job.Folder);
                Process.Start(new ProcessStartInfo(job.Folder) { UseShellExecute = true });
            }
            catch (Exception ex) { _details.Text = Loc.T("无法打开输出目录：") + ex.Message; }
        };
        _locate = new Button { Name = "LocateSource", Content = Loc.T("在卷中定位原片"), IsEnabled = false };
        _locate.Click += (_, _) => { if (_rows.SelectedItem is BatchExportReport.Row row) Close(new BatchReportAction(false, row.Item.SourcePath)); };
        var retry = new Button { Name = "RetryResults", Content = Loc.T("重试失败和未执行项"), IsEnabled = job.CanRetry };
        retry.Click += (_, _) => { if (job.CanRetry) Close(new BatchReportAction(true)); };
        var close = new Button { Content = Loc.T("关闭"), IsCancel = true, IsDefault = true };
        close.Click += (_, _) => Close();
        var actions = DialogLayout.Actions(folder, _locate, retry, close); Grid.SetRow(actions, 4); grid.Children.Add(actions);
        _rows.SelectionChanged += (_, _) =>
        {
            _details.Text = (_rows.SelectedItem as BatchExportReport.Row)?.Details ?? Loc.T("没有匹配的结果");
            _locate.IsEnabled = _rows.SelectedItem is BatchExportReport.Row row && canLocate(row.Item.SourcePath);
        };
        _filter.SelectionChanged += (_, _) => Refresh();
        Content = grid;
        Refresh();
    }

    private void Refresh()
    {
        var rows = _report.Rows((BatchResultFilter)_filter.SelectedIndex);
        _rows.ItemsSource = rows;
        _rows.SelectedIndex = rows.Count > 0 ? 0 : -1;
        _visibleCount.Text = Loc.F($"显示 {rows.Count} 项");
        if (rows.Count == 0) { _details.Text = Loc.T("没有匹配的结果"); _locate.IsEnabled = false; }
    }
}
