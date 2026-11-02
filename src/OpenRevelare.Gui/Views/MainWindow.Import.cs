using Avalonia.Input;
using Avalonia.Platform.Storage;
using OpenRevelare.Gui.Services;

namespace OpenRevelare.Gui.Views;

public partial class MainWindow
{
    private bool _dropImportPending;

    private void SetUpDropImport()
    {
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, (_, e) =>
        {
            bool accept = !_dropImportPending && Vm is { HasLongTask: false }
                && !_closePending && e.Data.Contains(DataFormats.Files);
            e.DragEffects = accept ? DragDropEffects.Copy : DragDropEffects.None;
            DropNotice.IsVisible = accept;
            e.Handled = true;
        }, handledEventsToo: true);
        AddHandler(DragDrop.DragLeaveEvent, (_, _) => DropNotice.IsVisible = false);
        AddHandler(DragDrop.DropEvent, OnFilesDropped, handledEventsToo: true);
    }

    private async void OnFilesDropped(object? sender, DragEventArgs e)
    {
        e.Handled = true;
        DropNotice.IsVisible = false;
        if (_dropImportPending || _closePending || Vm is not { HasLongTask: false } vm) return;
        var paths = e.Data.GetFiles()?.Select(f => f.TryGetLocalPath()).OfType<string>().ToArray();
        if (paths is not { Length: > 0 }) return;
        _dropImportPending = true;
        try
        {
            var files = await Task.Run(() => ImportFiles.Collect(paths));
            if (files.Projects.Length > 0)
            {
                if (files.Projects.Length != 1 || files.Images.Length > 0 || files.Rejected.Length > 0)
                {
                    await new InfoDialog(Loc.T("打开工程"), Loc.T("请单独拖入一个工程；图像可另行加入卷。"))
                        .ShowDialog(this);
                    return;
                }
                await vm.OpenProjectAsync(files.Projects[0]);
                vm.EnterDevelop();
                return;
            }
            if (files.Rejected.Length > 0 || files.Images.Length == 0)
            {
                await new InfoDialog(Loc.T("部分文件未加入"),
                    Loc.T("支持 RAW、TIFF、工程，或包含图像的文件夹。JPEG / PNG 不是负片输入。")
                    + "\n\n" + string.Join("\n", files.Rejected.Select(System.IO.Path.GetFileName)))
                    .ShowDialog(this);
            }
            if (files.Images.Length == 0) return;
            var dialog = new ImportDialog(files.Images, canAddToCurrent: vm.HasImage);
            if (await dialog.ShowDialog<bool>(this) != true || dialog.Result is not { } config) return;
            if (dialog.AddToCurrent) await vm.AddImagesAsync(config.Paths);
            else await ImportConfigurationAsync(config);
        }
        catch (Exception ex)
        {
            vm.StatusText = Loc.T("导入失败：") + ex.Message;
        }
        finally { _dropImportPending = false; }
    }
}
