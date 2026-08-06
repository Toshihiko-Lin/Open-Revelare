using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using OpenRevelare.Gui.Models;

namespace OpenRevelare.Gui.Views;

/// <summary>Modal field-group picker for broadcasts. Binds to a shared <see cref="SyncOptions"/>.</summary>
public partial class SyncDialog : Window
{
    public SyncDialog() => InitializeComponent();

    public SyncDialog(SyncOptions options) : this() => DataContext = options;

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();
}
