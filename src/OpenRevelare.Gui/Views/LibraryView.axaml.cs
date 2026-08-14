using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using OpenRevelare.Gui.Services;
using OpenRevelare.Gui.ViewModels;

namespace OpenRevelare.Gui.Views;

/// <summary>
/// The roll wall. Card interactions only — every consequence (opening a roll, switching back to
/// 修片) is raised as <see cref="OpenRequested"/> and carried out by the window, which is the one
/// that owns both modules.
/// </summary>
public partial class LibraryView : UserControl
{
    public LibraryView() => InitializeComponent();

    /// <summary>The user picked a roll to edit.</summary>
    public event Func<RollCard, Task>? OpenRequested;

    /// <summary>The user clicked the leading 「+ 新建卷」 tile.</summary>
    public event Func<Task>? NewRollRequested;

    private LibraryViewModel? Vm => DataContext as LibraryViewModel;

    private void OnClearFiltersClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => Vm?.ClearFilters();

    /// <summary>The card a menu item or gesture came from — every handler needs it, and the
    /// context menu's DataContext is the card itself.</summary>
    private static RollCard? CardOf(object? sender) => sender switch
    {
        Control c when c.DataContext is RollCard card => card,
        _ => null,
    };

    private async void OnNewTileTapped(object? sender, TappedEventArgs e)
    {
        if (NewRollRequested is { } handler) await handler();
    }

    private void OnCardPressed(object? sender, PointerPressedEventArgs e)
    {
        if (CardOf(sender) is { } card && Vm is { } vm) vm.Selected = card;
    }

    private async void OnCardDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (CardOf(sender) is { } card) await Open(card);
    }

    private async void OnOpenClick(object? sender, RoutedEventArgs e)
    {
        if (CardOf(sender) is { } card) await Open(card);
    }

    private async Task Open(RollCard card)
    {
        if (card.Roll is not { } roll) return;
        if (card.Missing)
        {
            await new InfoDialog(Loc.T("卷不可用"),
                    Loc.F($"找不到工程文件：\n{roll.ProjectPath}\n\n如果只是移动了位置，可以用「文件 → 打开工程…」重新指向它。"))
                .ShowDialog(Root());
            return;
        }
        if (OpenRequested is { } handler) await handler(card);
    }

    private async void OnRenameClick(object? sender, RoutedEventArgs e)
    {
        if (CardOf(sender) is not { } card || Vm is not { } vm) return;
        string? name = await new PromptDialog(Loc.T("重命名卷"), Loc.T("卷名"), card.Title).ShowDialog<string?>(Root());
        if (name is null) return;
        if (vm.Rename(card, name) is { } error)
            await new InfoDialog(Loc.T("重命名失败"), error).ShowDialog(Root());
    }

    private async void OnInfoClick(object? sender, RoutedEventArgs e)
    {
        if (CardOf(sender) is not { } card || Vm is not { } vm) return;
        if (vm.LoadNotes(card) is not { } current)
        {
            await new InfoDialog(Loc.T("读不到卷信息"), Loc.T("无法读取该卷的工程文件。")).ShowDialog(Root());
            return;
        }
        var edited = await new RollInfoDialog(card.Title, current)
            .ShowDialog<OpenRevelare.Gui.Models.RollNotes?>(Root());
        if (edited is null) return;
        if (vm.SaveNotes(card, edited) is { } error)
            await new InfoDialog(Loc.T("保存失败"), error).ShowDialog(Root());
    }

    /// <summary>
    /// Save this roll's stored cover sheet somewhere. Deliberately a COPY of the cover rather than
    /// a fresh render: the cover is already the full 2048-px sheet with its info bar, and
    /// re-rendering from the wall would mean decoding the entire roll for a menu click. Use
    /// 文件 → 导出印样 with the roll open when a from-scratch export is wanted.
    /// </summary>
    private async void OnExportSheetClick(object? sender, RoutedEventArgs e)
    {
        if (CardOf(sender) is not { } card || card.Roll is not { } roll) return;
        string source = SheetStore.PathFor(roll.Id);
        if (!File.Exists(source))
        {
            await new InfoDialog(Loc.T("还没有印样"),
                    Loc.T("这一卷还没有生成过印样封面。打开它一次，封面会自动生成。")).ShowDialog(Root());
            return;
        }
        var file = await Root().StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = Loc.T("导出印样"),
            DefaultExtension = "jpg",
            SuggestedFileName = card.Title,
            FileTypeChoices = new[] { new FilePickerFileType("JPEG") { Patterns = new[] { "*.jpg", "*.jpeg" } } },
        });
        if (file?.TryGetLocalPath() is not { } target) return;
        try { File.Copy(source, target, overwrite: true); }
        catch (Exception ex) { await new InfoDialog(Loc.T("导出失败"), ex.Message).ShowDialog(Root()); }
    }

    /// <summary>Point a moved roll at its project file's new location. Keeps the entry (and so
    /// its cover and its place in the wall) — this is a correction, not a re-import.</summary>
    private async void OnRelocateClick(object? sender, RoutedEventArgs e)
    {
        if (CardOf(sender) is not { } card || card.Roll is not { } roll || Vm is not { } vm) return;
        var files = await Root().StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = Loc.F($"「{roll.Title}」的工程文件现在在哪"),
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType(Loc.T("OpenRevelare 工程 (.ncproj)")) { Patterns = new[] { "*.ncproj" } },
            },
        });
        if (files.FirstOrDefault()?.TryGetLocalPath() is not { } path) return;

        Catalog.Relocate(roll, path);
        card.RefreshText();
        await vm.RefreshCoverAsync(card);
    }

    /// <summary>
    /// Show the roll's project file in the system file manager.
    ///
    /// Arguments go through <see cref="ProcessStartInfo.ArgumentList"/> rather than the single
    /// <c>Arguments</c> string on the two Unix platforms, so the path is handed over as one argv
    /// entry and is never re-parsed.
    ///
    /// Quoting it into a string works for ordinary paths — .NET's splitter does strip the outer
    /// quotes, spaces and CJK included — but it is the Windows command-line grammar, and two
    /// characters that are perfectly legal in a macOS or Linux filename do not survive it
    /// (measured, not assumed): a <c>"</c> in the path is eaten outright, and a trailing
    /// <c>\</c> escapes the closing quote and turns into one. Either way the file manager is
    /// asked to reveal a path that does not exist, and the failure is silent.
    ///
    /// Windows is the exception and keeps the raw string: explorer.exe does not take a normal
    /// argv, it wants the literal <c>/select,"C:\path"</c> with the comma and quotes intact.
    /// </summary>
    private void OnRevealClick(object? sender, RoutedEventArgs e)
    {
        if (CardOf(sender) is not { } card || card.Roll is not { } roll) return;
        try
        {
            // /select, puts the project file itself in view rather than just opening the folder.
            if (OperatingSystem.IsWindows())
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{roll.ProjectPath}\""));
            else if (OperatingSystem.IsMacOS())
                Process.Start(new ProcessStartInfo("open")
                { ArgumentList = { "-R", roll.ProjectPath } });
            else if (System.IO.Path.GetDirectoryName(roll.ProjectPath) is { } dir)
                Process.Start(new ProcessStartInfo("xdg-open") { ArgumentList = { dir } });
        }
        // Still no dialog — a machine without a file manager is not an error worth interrupting
        // anyone for. But swallowing it without a trace is how this stayed "clicking does
        // nothing" for whoever had to report it, so leave something a log can show.
        catch (Exception ex)
        {
            Debug.WriteLine($"[reveal] {roll.ProjectPath}: {ex.Message}");
        }
    }

    private async void OnForgetClick(object? sender, RoutedEventArgs e)
    {
        if (CardOf(sender) is not { } card || Vm is not { } vm) return;
        if (!await Confirm(Loc.T("从目录移除"),
                Loc.F($"「{card.Title}」将从目录中移除。\n\n工程文件与照片都不会被删除——重新用「打开工程…」指向它即可回到目录。")))
            return;
        vm.Remove(card, deleteProject: false);
    }

    private async void OnDeleteClick(object? sender, RoutedEventArgs e)
    {
        if (CardOf(sender) is not { } card || card.Roll is not { } roll || Vm is not { } vm) return;
        if (!await Confirm(Loc.T("删除工程文件"),
                Loc.F($"将删除「{card.Title}」的工程文件：\n{roll.ProjectPath}\n\n这一卷的全部调整都会丢失，且无法撤销。照片本身不会被删除。")))
            return;
        vm.Remove(card, deleteProject: true);
    }

    /// <summary>Two-button confirmation. Destructive actions here delete an edit, so neither is
    /// allowed to happen on a single click.</summary>
    private async Task<bool> Confirm(string title, string body)
    {
        bool ok = false;
        await new InfoDialog(title, body).WithAction(Loc.T("继续"), Loc.T("取消"), () => ok = true).ShowDialog(Root());
        return ok;
    }

    private Window Root() => (Window)TopLevel.GetTopLevel(this)!;
}
