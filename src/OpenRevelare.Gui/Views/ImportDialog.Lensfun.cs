using Avalonia.Threading;
using OpenRevelare.Core;
using OpenRevelare.Gui.Services;

namespace OpenRevelare.Gui.Views;

public partial class ImportDialog
{
    private readonly ImportLensPreview _lensPreview = new();
    private bool _lensRefreshQueued, _lensClosed;

    private void SetUpLensDetection()
    {
        // Conditional RGB/LCC controls can grow the dialog after its initial screen clamp.
        SizeChanged += (_, _) => Dispatcher.UIThread.Post(() =>
        {
            if (!_lensClosed && IsVisible) WindowLayout.FitToScreen(this);
        }, DispatcherPriority.Loaded);
        Files.CollectionChanged += (_, _) => QueueLensDetection();
        Closed += (_, _) => _lensClosed = true;
        QueueLensDetection();
    }

    private void QueueLensDetection()
    {
        if (_lensRefreshQueued || _lensClosed) return;
        _lensRefreshQueued = true;
        // Coalesce folder additions. Detection has no import UI: results and the enable choice
        // belong to the main lens-correction panel after the roll has loaded.
        Dispatcher.UIThread.Post(async () =>
        {
            _lensRefreshQueued = false;
            if (_lensClosed || AddToCurrent) return;
            try { await _lensPreview.Get(Files.FirstOrDefault()); }
            catch (Exception) { /* Failed metadata must not block an uncorrected import. */ }
        });
    }

    private async Task<(bool Current, LensfunCalibration? Calibration)> FinishLensDetectionAsync()
    {
        if (AddToCurrent) return (!_lensClosed, null); // appended frames inherit the current roll
        string? first = Files.FirstOrDefault();
        LensfunCalibration? calibration = null;
        try
        {
            var result = await _lensPreview.Get(first);
            if (_lensClosed || first != Files.FirstOrDefault()) return (false, null);
            if (result?.Identification.Profile is { } profile)
                calibration = result.Database.CreateCalibration(profile, result.Capture);
        }
        catch (Exception) { /* Failed metadata must not block an uncorrected import. */ }
        return (!_lensClosed && first == Files.FirstOrDefault(), calibration);
    }
}
