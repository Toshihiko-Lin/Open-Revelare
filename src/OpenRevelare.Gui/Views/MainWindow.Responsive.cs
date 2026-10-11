using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;

namespace OpenRevelare.Gui.Views;

public partial class MainWindow
{
    private bool? _stackedWorkspace;

    private void UpdateWorkspaceLayout()
    {
        double width = Bounds.Width > 0 ? Bounds.Width : Width;
        bool stacked = width < 1100;
        if (_stackedWorkspace != stacked)
        {
            _stackedWorkspace = stacked;
            WorkspaceGrid.ColumnDefinitions = new ColumnDefinitions(stacked
                ? "120,Auto,*,0,0" : "140,Auto,*,Auto,410");
            WorkspaceGrid.RowDefinitions = new RowDefinitions(stacked ? "Auto,Auto" : "*,Auto");
            // Splitters cannot squeeze controls beneath their usable width.
            WorkspaceGrid.ColumnDefinitions[0].MinWidth = 120;
            WorkspaceGrid.ColumnDefinitions[0].MaxWidth = 220;
            WorkspaceGrid.ColumnDefinitions[2].MinWidth = 480;
            if (!stacked)
            {
                WorkspaceGrid.ColumnDefinitions[4].MinWidth = 360;
                WorkspaceGrid.ColumnDefinitions[4].MaxWidth = 520;
            }
            Grid.SetRow(InspectorWorkspace, stacked ? 1 : 0);
            Grid.SetColumn(InspectorWorkspace, stacked ? 2 : 4);
            Grid.SetRowSpan(FilmRail, stacked ? 2 : 1);
            Grid.SetRowSpan(FilmRailSplitter, stacked ? 2 : 1);
            InspectorWorkspace.Margin = stacked ? new Thickness(8,12,8,0) : new Thickness(8,0,0,0);
            InspectorWorkspace.Height = stacked ? 620 : double.NaN;
            PreviewWorkspace.VerticalAlignment = stacked ? Avalonia.Layout.VerticalAlignment.Top : Avalonia.Layout.VerticalAlignment.Stretch;
            InspectorWorkspace.VerticalAlignment = stacked ? Avalonia.Layout.VerticalAlignment.Top : Avalonia.Layout.VerticalAlignment.Stretch;
            FilmRail.VerticalAlignment = stacked ? Avalonia.Layout.VerticalAlignment.Top : Avalonia.Layout.VerticalAlignment.Stretch;
            InspectorSplitter.IsVisible = !stacked;
            WorkspaceScroll.VerticalScrollBarVisibility = stacked ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled;
            WorkspaceScroll.Offset = default;
        }
        PreviewWorkspace.Height = stacked
            ? Math.Clamp((Bounds.Height > 0 ? Bounds.Height : Height) - 160, 340, 520)
            : double.NaN;
        // Keep thumbnail virtualization bounded even when the outer workspace scrolls.
        FilmRail.Height = stacked ? PreviewWorkspace.Height + InspectorWorkspace.Height + 12 : double.NaN;
    }
}
