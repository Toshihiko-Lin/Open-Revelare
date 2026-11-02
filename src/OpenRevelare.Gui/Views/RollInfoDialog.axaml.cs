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
    private readonly RollNotes _initial;
    private readonly TextBox _camera = new(), _film = new(), _number = new();
    private readonly TextBox _lab = new(), _date = new(), _place = new();
    private readonly TextBox _note = new() { AcceptsReturn = true, Height = 66, TextWrapping = TextWrapping.Wrap };
    private readonly AutoCompleteBox _format = new() { FilterMode = AutoCompleteFilterMode.None };

    public RollInfoDialog(string rollTitle, RollNotes initial)
    {
        _initial = initial.Clone();
        Title = Loc.T("卷信息");
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
        string[] labels = { Loc.T("画幅"), Loc.T("相机"), Loc.T("胶卷"), Loc.T("卷号"), Loc.T("冲洗店"), Loc.T("日期"), Loc.T("地点") };
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

        var ok = new Button { Content = Loc.T("保存"), IsDefault = true, Classes = { "primary" } };
        var cancel = new Button { Content = Loc.T("取消"), IsCancel = true };
        ok.Click += (_, _) => Close(Collect());
        cancel.Click += (_, _) => Close(null);

        var panel = new StackPanel { Spacing = 4 };
        panel.Children.Add(new TextBlock { Text = rollTitle, Classes = { "dialogTitle" } });
        panel.Children.Add(grid);
        panel.Children.Add(new TextBlock { Text = Loc.T("备注"), Margin = new Thickness(0, 8, 0, 3) });
        panel.Children.Add(_note);
        Content = DialogLayout.Compose(panel, DialogLayout.Actions(cancel, ok));

        Opened += (_, _) => { _camera.Focus(); };
        KeyDown += (_, e) => { if (e.Key == Key.Escape) Close(null); };
    }

    private RollNotes Collect() => MergeVisibleFields(_initial, new()
    {
        CameraBody = _camera.Text ?? "", FilmStock = _film.Text ?? "",
        RollNumber = _number.Text ?? "",
        DevLab = _lab.Text ?? "",
        DevDate = _date.Text ?? "", Location = _place.Text ?? "",
        RollNote = _note.Text ?? "",
        Format = _format.Text ?? "",
    });

    // A partial editor must not blank metadata entered from the contact-sheet editor (D13).
    internal static RollNotes MergeVisibleFields(RollNotes initial, RollNotes edited)
    {
        var result = initial.Clone();
        result.CameraBody = edited.CameraBody; result.FilmStock = edited.FilmStock;
        result.RollNumber = edited.RollNumber; result.DevLab = edited.DevLab;
        result.DevDate = edited.DevDate; result.Location = edited.Location;
        result.RollNote = edited.RollNote; result.Format = edited.Format;
        return result;
    }
}
