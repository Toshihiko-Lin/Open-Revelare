using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using OpenRevelare.Gui.Models;

namespace OpenRevelare.Gui.Views;

/// <summary>
/// Edit a roll's annotation — the fields burned onto its contact sheet's info bar, and the source
/// of its subtitle in the catalog.
///
/// Reachable from the 图库 card, so it works on a roll that is NOT open: the caller loads the
/// values out of that roll's .ncproj and writes them back. The same nine fields also live in the
/// 印样 dialog, where you edit them while looking at the sheet they label; this is the way in when
/// you are looking at the wall instead.
/// </summary>
public sealed class RollInfoDialog : Window
{
    private readonly TextBox _camera = new(), _film = new(), _number = new();
    private readonly TextBox _lab = new(), _date = new(), _place = new();
    private readonly TextBox _note = new() { AcceptsReturn = true, Height = 66, TextWrapping = TextWrapping.Wrap };
    private readonly AutoCompleteBox _format = new() { FilterMode = AutoCompleteFilterMode.None };

    public RollInfoDialog(string rollTitle, RollNotes initial)
    {
        Title = "卷信息";
        Width = 420;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        _format.ItemsSource = ImportDialog.FormatPresets;
        _format.Text = initial.Format;

        _camera.Text = initial.CameraBody; _film.Text = initial.FilmStock;
        _number.Text = initial.RollNumber;
        _lab.Text = initial.DevLab;
        _date.Text = initial.DevDate; _place.Text = initial.Location;
        _note.Text = initial.RollNote;

        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*"),
            RowDefinitions = new RowDefinitions(string.Join(",", new string[7].Select(_ => "Auto"))),
        };
        string[] labels = { "画幅", "相机", "胶卷", "卷号", "冲洗店", "日期", "地点" };
        Control[] boxes = { _format, _camera, _film, _number, _lab, _date, _place };
        for (int i = 0; i < labels.Length; i++)
        {
            var t = new TextBlock
            {
                Text = labels[i],
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 3, 12, 3),
            };
            Grid.SetRow(t, i); Grid.SetColumn(t, 0);
            boxes[i].Margin = new Thickness(0, 3);
            Grid.SetRow(boxes[i], i); Grid.SetColumn(boxes[i], 1);
            grid.Children.Add(t);
            grid.Children.Add(boxes[i]);
        }

        var ok = new Button { Content = "保存", IsDefault = true, Margin = new Thickness(0, 0, 6, 0) };
        var cancel = new Button { Content = "取消", IsCancel = true };
        ok.Click += (_, _) => Close(Collect());
        cancel.Click += (_, _) => Close(null);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 14, 0, 0),
        };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);

        var panel = new StackPanel { Margin = new Thickness(18), Spacing = 4 };
        panel.Children.Add(new TextBlock { Text = rollTitle, FontSize = 15, FontWeight = FontWeight.Bold });
        panel.Children.Add(new TextBlock
        {
            Text = "这些信息会烧在该卷印样底部的标识条上，并作为图库卡片的副标题。",
            FontSize = 11, Foreground = Brushes.Gray, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 8),
        });
        panel.Children.Add(grid);
        panel.Children.Add(new TextBlock { Text = "备注", Margin = new Thickness(0, 8, 0, 3) });
        panel.Children.Add(_note);
        panel.Children.Add(buttons);
        Content = new ScrollViewer { Content = panel };

        Opened += (_, _) => { _camera.Focus(); };
        KeyDown += (_, e) => { if (e.Key == Key.Escape) Close(null); };
    }

    private RollNotes Collect() => new()
    {
        CameraBody = _camera.Text ?? "", FilmStock = _film.Text ?? "",
        RollNumber = _number.Text ?? "",
        DevLab = _lab.Text ?? "",
        DevDate = _date.Text ?? "", Location = _place.Text ?? "",
        RollNote = _note.Text ?? "",
        Format = _format.Text ?? "",
    };
}
