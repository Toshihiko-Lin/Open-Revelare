using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Layout;

namespace OpenRevelare.Gui.Views;

public partial class MainWindow
{
    private (int Mode, bool Rail)? _workspaceLayout;
    private bool _compactFilmRail;
    private bool? _shortWorkspace;
    private bool _analysisBeforeShort = true;

    private void OnCompactFilmRailClick(object? sender, RoutedEventArgs e)
    {
        _compactFilmRail = CompactFilmRailButton.IsChecked == true;
        UpdateWorkspaceLayout();
    }

    private void UpdateWorkspaceLayout()
    {
        double width = Bounds.Width > 0 ? Bounds.Width : Width;
        bool shortWindow = (Bounds.Height > 0 ? Bounds.Height : Height) < 650;
        if (_shortWorkspace != shortWindow)
        {
            if (shortWindow) { _analysisBeforeShort = AnalysisExpander.IsExpanded; AnalysisExpander.IsExpanded = false; }
            else if (_shortWorkspace == true) AnalysisExpander.IsExpanded = _analysisBeforeShort;
            _shortWorkspace = shortWindow;
        }
        int mode = width < 1000 ? 0 : width < 1240 ? 1 : 2;
        bool rail = mode != 0 || _compactFilmRail;
        CompactFilmRailButton.IsVisible = mode == 0;
        CompactFilmRailButton.IsChecked = _compactFilmRail;
        FilmRail.IsVisible = rail && Vm?.HasImage == true;
        FilmRailSplitter.IsVisible = rail;
        if (_workspaceLayout == (mode, rail)) return;
        _workspaceLayout = (mode, rail);
        NavigationBar.ColumnDefinitions = new ColumnDefinitions(mode == 0 ? "*,Auto" : "*,Auto,*");
        NavigationBar.RowDefinitions = new RowDefinitions(mode == 0 ? "Auto,Auto" : "Auto");
        Grid.SetRow(NavigationActions, mode == 0 ? 1 : 0);
        Grid.SetColumn(NavigationActions, mode == 0 ? 0 : 2);
        Grid.SetColumnSpan(NavigationActions, mode == 0 ? 2 : 1);
        WorkspaceGrid.ColumnDefinitions = new ColumnDefinitions(mode switch
        {
            2 => "140,Auto,*,Auto,410",
            1 => "120,Auto,*,Auto,340",
            _ when rail => "120,Auto,*,Auto,320",
            _ => "0,0,*,Auto,320",
        });
        WorkspaceGrid.RowDefinitions = new RowDefinitions("*,Auto");
        WorkspaceGrid.ColumnDefinitions[0].MinWidth = rail ? 120 : 0;
        WorkspaceGrid.ColumnDefinitions[0].MaxWidth = rail ? 220 : 0;
        WorkspaceGrid.ColumnDefinitions[2].MinWidth = mode == 0 ? 260 : 400;
        WorkspaceGrid.ColumnDefinitions[4].MinWidth = mode == 2 ? 360 : 320;
        WorkspaceGrid.ColumnDefinitions[4].MaxWidth = mode == 2 ? 520 : 400;
        Grid.SetRow(InspectorWorkspace, 0);
        Grid.SetColumn(InspectorWorkspace, 4);
        Grid.SetRowSpan(FilmRail, 1);
        Grid.SetRowSpan(FilmRailSplitter, 1);
        InspectorWorkspace.Margin = new Thickness(8, 0, 0, 0);
        InspectorWorkspace.Height = double.NaN;
        PreviewWorkspace.Height = double.NaN;
        FilmRail.Height = double.NaN;
        PreviewWorkspace.VerticalAlignment = VerticalAlignment.Stretch;
        InspectorWorkspace.VerticalAlignment = VerticalAlignment.Stretch;
        FilmRail.VerticalAlignment = VerticalAlignment.Stretch;
        InspectorSplitter.IsVisible = mode != 0;
        WorkspaceScroll.VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
        WorkspaceScroll.Offset = default;
    }
}
