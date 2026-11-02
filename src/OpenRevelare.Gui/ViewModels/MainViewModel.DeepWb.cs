using CommunityToolkit.Mvvm.ComponentModel;
using OpenRevelare.DeepWb.Onnx;
using OpenRevelare.Gui.Services;

namespace OpenRevelare.Gui.ViewModels;

public partial class MainViewModel
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NeedsDeepWbModel), nameof(DeepWbInstallHint))]
    private bool _isDeepWbInstalled = DeepWbModel.IsInstalled;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DeepWbInstallHint))]
    private bool _isInstallingDeepWb;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DeepWbInstallHint))]
    private int _deepWbDownloadPercent;

    public bool PreferDeepWbMirror { get; set; }

    public bool NeedsDeepWbModel => !IsDeepWbInstalled;
    public string DeepWbInstallHint => IsInstallingDeepWb
        ? Loc.F($"正在下载模型（{DeepWbDownloadPercent}%）…")
        : Loc.T("需下载模型 · 17.5 MB");

    public void RefreshDeepWbAvailability()
    {
        if (!IsInstallingDeepWb) IsDeepWbInstalled = DeepWbModel.IsInstalled;
    }

    public async Task<bool> InstallDeepWbAsync()
    {
        RefreshDeepWbAvailability();
        if (IsDeepWbInstalled) return true;
        if (!TryBeginLongTask(Loc.T("准备下载 Deep-WB 模型 …"), out var owner)) return false;
        try
        {
            if (ConfirmDeepWbDownload is not { } confirm || !await confirm())
            {
                StatusText = Loc.T("Deep-WB 模型未安装");
                return false;
            }
            owner.Token.ThrowIfCancellationRequested();
            IsInstallingDeepWb = true;
            DeepWbDownloadPercent = 0;
            var progress = new Progress<int>(percent =>
            {
                if (ReferenceEquals(_longTaskCts, owner) && IsInstallingDeepWb)
                {
                    DeepWbDownloadPercent = percent;
                    ReportLongTask(Loc.F($"正在下载 Deep-WB 模型（{percent}%）…"));
                }
            });
            await DeepWbModel.InstallAsync(progress, owner.Token, PreferDeepWbMirror);
            StatusText = Loc.T("Deep-WB 模型已安装，可使用智能色偏修正。");
            return true;
        }
        catch (OperationCanceledException) { StatusText = Loc.T("Deep-WB 模型下载已取消"); return false; }
        catch (Exception ex) { StatusText = Loc.T("Deep-WB 模型下载失败，可点击下载图标重试：") + ex.Message; return false; }
        finally
        {
            IsInstallingDeepWb = false;
            RefreshDeepWbAvailability();
            EndLongTask(owner);
        }
    }
}
