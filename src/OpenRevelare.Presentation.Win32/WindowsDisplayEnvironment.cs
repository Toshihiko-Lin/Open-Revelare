using OpenRevelare.ColorManagement;
using OpenRevelare.Presentation;

namespace OpenRevelare.Presentation.Win32;

/// <summary>
/// A manually refreshed Win32 display contract. GUI integration should call <see cref="Refresh"/>
/// for display/profile/DPI/Advanced Color notifications; this project deliberately owns no
/// Avalonia message hook or polling thread.
/// </summary>
public interface IWindowsDisplayEnvironment : IDisplayEnvironment, IDisposable
{
    WindowsDisplayDiagnostics Diagnostics { get; }
    bool Refresh();
}

public sealed class WindowsDisplayEnvironment :
    IWindowsDisplayEnvironment,
    IWindowsDisplayContractLeaseProvider
{
    private readonly object _stateGate = new();
    private readonly object _refreshGate = new();
    private readonly nint _windowHwnd;
    private readonly IWindowsDisplayProbe _probe;
    private readonly IColorManagementEngine _colorManagement;
    private readonly Dictionary<ProfileIdentity, string?> _monitorProfileValidation = [];
    private DisplayContract _current;
    private WindowsDisplayDiagnostics _diagnostics;
    private WindowsDisplaySemanticKey _semanticKey;
    private long _revision;
    private bool _disposed;

    public WindowsDisplayEnvironment(
        nint windowHwnd,
        IColorManagementEngine colorManagement)
        : this(windowHwnd, CreateSystemProbe(windowHwnd), colorManagement)
    {
    }

    internal WindowsDisplayEnvironment(
        nint windowHwnd,
        IWindowsDisplayProbe probe,
        IColorManagementEngine colorManagement)
    {
        ArgumentNullException.ThrowIfNull(probe);
        ArgumentNullException.ThrowIfNull(colorManagement);
        _windowHwnd = windowHwnd;
        _probe = probe;
        _colorManagement = colorManagement;

        WindowsDisplayProbeSnapshot initial = SafeProbe();
        _revision = 1;
        _semanticKey = initial.SemanticKey;
        _current = WindowsDisplayContractProvider.Build(initial, _revision);
        _diagnostics = WindowsDisplayContractProvider.Diagnostics(initial, _current);
    }

    public DisplayContract Current
    {
        get
        {
            lock (_stateGate)
            {
                ThrowIfDisposed();
                return _current;
            }
        }
    }

    public WindowsDisplayDiagnostics Diagnostics
    {
        get
        {
            lock (_stateGate)
            {
                ThrowIfDisposed();
                return _diagnostics;
            }
        }
    }

    public event EventHandler<DisplayContract>? ContractChanged;

    void IWindowsDisplayContractLeaseProvider.WithStableContract(Action<DisplayContract> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        lock (_refreshGate)
        {
            DisplayContract current;
            lock (_stateGate)
            {
                ThrowIfDisposed();
                current = _current;
            }
            action(current);
        }
    }

    /// <returns><see langword="true"/> only when a semantic display contract change was published.</returns>
    public bool Refresh()
    {
        DisplayContract? changed = null;
        lock (_refreshGate)
        {
            WindowsDisplayProbeSnapshot next = SafeProbe();
            lock (_stateGate)
            {
                ThrowIfDisposed();
                if (next.SemanticKey == _semanticKey)
                {
                    _diagnostics = WindowsDisplayContractProvider.Diagnostics(next, _current);
                    return false;
                }

                if (_revision == long.MaxValue)
                    throw new InvalidOperationException("Windows display contract revision overflowed.");
                _revision++;
                _semanticKey = next.SemanticKey;
                _current = WindowsDisplayContractProvider.Build(next, _revision);
                _diagnostics = WindowsDisplayContractProvider.Diagnostics(next, _current);
                changed = _current;
            }
        }

        ContractChanged?.Invoke(this, changed!);
        return true;
    }

    public void Dispose()
    {
        lock (_stateGate) _disposed = true;
    }

    private WindowsDisplayProbeSnapshot SafeProbe()
    {
        try
        {
            return ValidateMonitorProfile(_probe.Probe(_windowHwnd));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return WindowsDisplayProbeSnapshot.Failed(
                _windowHwnd, $"Win32 display probe failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private WindowsDisplayProbeSnapshot ValidateMonitorProfile(
        WindowsDisplayProbeSnapshot snapshot)
    {
        // Advanced Color owns the final transform and therefore does not consume the legacy
        // monitor ICC. Only an inactive, reliable Advanced Color state can publish an
        // application-managed legacy contract.
        if (!snapshot.AdvancedColor.IsReliable ||
            snapshot.AdvancedColor.Active ||
            snapshot.MonitorProfile is not { } profile)
        {
            return snapshot;
        }

        if (!_monitorProfileValidation.TryGetValue(profile.Identity, out string? failure))
        {
            failure = ValidateMonitorProfileTransform(snapshot, profile);
            _monitorProfileValidation.Add(profile.Identity, failure);
        }

        if (failure is null) return snapshot;
        return snapshot with
        {
            MonitorProfile = null,
            MonitorProfileFailureReason = failure,
        };
    }

    private string? ValidateMonitorProfileTransform(
        WindowsDisplayProbeSnapshot snapshot,
        MonitorProfileData profile)
    {
        try
        {
            ColorProfileRef deviceProfile = ColorProfileRef.Create(
                profile.IccBytes,
                profile.Description,
                ProfileRole.Monitor,
                new ProfileSource.Monitor(snapshot.DisplayId, revision: 0));
            ProfileValidationResult validation = _colorManagement.Validate(deviceProfile);
            if (!validation.IsValid)
            {
                return "The exact active monitor ICC failed shared LittleCMS validation: " +
                    validation.Message;
            }

            var request = new ColorTransformRequest(
                PcsColorProfiles.D50Xyz,
                deviceProfile,
                TransformPurpose.MonitorPresentation,
                RenderingIntent.RelativeColorimetric,
                blackPointCompensation: true,
                adaptationState: 1.0,
                sourceFormat: PixelFormatDescriptor.XyzFloat32,
                destinationFormat: PixelFormatDescriptor.RgbFloat32);
            Span<float> destination = stackalloc float[3];
            using (IColorTransformLease transform = _colorManagement.Lease(request))
            {
                transform.Apply([0.18f, 0.18f, 0.18f], destination, pixelCount: 1);
            }

            if (!float.IsFinite(destination[0]) ||
                !float.IsFinite(destination[1]) ||
                !float.IsFinite(destination[2]))
            {
                return "The exact active monitor ICC transform produced non-finite RGB values.";
            }
            return null;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return $"The exact active monitor ICC is not transform-viable in shared LittleCMS: " +
                $"{ex.GetType().Name}: {ex.Message}";
        }
    }

    private static IWindowsDisplayProbe CreateSystemProbe(nint windowHwnd)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("The Windows display environment is Windows-only.");
        if (windowHwnd == nint.Zero) throw new ArgumentException("Window HWND is null.", nameof(windowHwnd));
        return new Win32DisplayProbe();
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}

internal static class WindowsDisplayContractProvider
{
    internal static DisplayContract Build(WindowsDisplayProbeSnapshot snapshot, long revision)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (revision < 0) throw new ArgumentOutOfRangeException(nameof(revision));

        float sdrWhite = float.IsFinite(snapshot.SdrWhiteNits) && snapshot.SdrWhiteNits > 0
            ? snapshot.SdrWhiteNits
            : 80f;

        if (!string.IsNullOrWhiteSpace(snapshot.FailureReason) || !snapshot.AdvancedColor.IsReliable)
        {
            string warning = snapshot.FailureReason ?? snapshot.AdvancedColor.FailureReason ??
                "Windows display color state is unavailable.";
            return new DisplayContract(
                snapshot.DisplayId,
                revision,
                PresentationEncoding.UnmanagedEmergencySrgb8,
                FinalTransformOwner.None,
                null,
                sdrWhite,
                1f,
                1f,
                "Windows unmanaged emergency",
                warning);
        }

        if (snapshot.AdvancedColor.Active)
        {
            if (snapshot.AdvancedColor.Mode is not (WindowsAdvancedColorMode.WideColorGamut or
                WindowsAdvancedColorMode.HighDynamicRange))
            {
                return new DisplayContract(
                    snapshot.DisplayId,
                    revision,
                    PresentationEncoding.UnmanagedEmergencySrgb8,
                    FinalTransformOwner.None,
                    null,
                    sdrWhite,
                    1f,
                    1f,
                    "Windows inconsistent Advanced Color state",
                    "Windows reports Advanced Color active without a WCG/HDR composition mode.");
            }

            return new DisplayContract(
                snapshot.DisplayId,
                revision,
                PresentationEncoding.LinearExtendedSrgbRgba16F,
                FinalTransformOwner.SystemCompositor,
                null,
                sdrWhite,
                AdvancedColorReferenceWhiteScale(snapshot.AdvancedColor.Mode, sdrWhite),
                1f,
                $"Windows Advanced Color · {snapshot.AdvancedColor.Mode}");
        }

        if (snapshot.MonitorProfile is not { } profile)
        {
            string warning = snapshot.MonitorProfileFailureReason ??
                "Advanced Color is inactive, but the exact active monitor ICC profile is unavailable.";
            return new DisplayContract(
                snapshot.DisplayId,
                revision,
                PresentationEncoding.UnmanagedEmergencySrgb8,
                FinalTransformOwner.None,
                null,
                sdrWhite,
                1f,
                1f,
                "Windows unmanaged emergency",
                warning);
        }

        ColorProfileRef profileRef = ColorProfileRef.Create(
            profile.IccBytes,
            profile.Description,
            ProfileRole.Monitor,
            new ProfileSource.Monitor(snapshot.DisplayId, revision));
        return new DisplayContract(
            snapshot.DisplayId,
            revision,
            PresentationEncoding.MonitorDeviceBgra8,
            FinalTransformOwner.Application,
            profileRef,
            sdrWhite,
            1f,
            1f,
            $"Windows app-managed · {profile.FileName} [{profileRef.Identity.Sha256Hex[..12]}]");
    }

    /// <summary>
    /// The canonical reference-white scale for an active Advanced Color mode (D-020).
    ///
    /// <para>
    /// THE TWO MODES ARE NOT THE SAME POLICY. Advanced Color SDR (WCG) composition is
    /// display-referred: canonical <c>1.0</c> is always the maximum white the panel can
    /// reproduce, and a reference-white level does not apply at all, so the only correct scale
    /// is exactly <c>1.0</c>. HDR composition is scene-referred: canonical <c>1.0</c> is always
    /// the nominal 80 nits, and the user-configured SDR reference white (commonly around
    /// 200 nits on a desktop HDR monitor) is where diffuse white belongs. Presenting an
    /// unscaled SDR render on an HDR display therefore reproduces it at 80 nits — correct
    /// colour, far too dim.
    /// </para>
    ///
    /// <para>
    /// D-011 pinned this to a constant <c>1.0</c>, which is right for WCG and wrong for HDR;
    /// that is why HDR had to fail closed. The scale is contract data only — the shared
    /// <c>PresentationBufferBuilder</c> applies it exactly once, and no platform presenter is
    /// permitted to hide an exposure multiplier (§9.2).
    /// </para>
    /// </summary>
    internal static float AdvancedColorReferenceWhiteScale(
        WindowsAdvancedColorMode mode,
        float sdrWhiteNits) => mode switch
    {
        WindowsAdvancedColorMode.WideColorGamut => 1f,
        WindowsAdvancedColorMode.HighDynamicRange =>
            sdrWhiteNits / DisplayContract.CanonicalNominalWhiteNits,
        _ => throw new ArgumentOutOfRangeException(
            nameof(mode),
            mode,
            "Only an active Advanced Color mode has a canonical reference-white scale."),
    };

    internal static WindowsDisplayDiagnostics Diagnostics(
        WindowsDisplayProbeSnapshot snapshot,
        DisplayContract contract)
    {
        WindowsMonitorProfileStatus profileStatus = contract.Encoding switch
        {
            PresentationEncoding.LinearExtendedSrgbRgba16F =>
                WindowsMonitorProfileStatus.OwnedBySystemAdvancedColor,
            PresentationEncoding.MonitorDeviceBgra8 =>
                WindowsMonitorProfileStatus.ExactLegacyProfile,
            _ => WindowsMonitorProfileStatus.Unavailable,
        };
        MonitorProfileData? profile = snapshot.MonitorProfile;
        return new WindowsDisplayDiagnostics(
            snapshot.DisplayId,
            contract.Revision,
            snapshot.AdvancedColor.ProbeApi,
            snapshot.GdiDeviceName,
            snapshot.MonitorDevicePath,
            snapshot.MonitorFriendlyName,
            snapshot.AdapterLuid,
            snapshot.SourceId,
            snapshot.TargetId,
            snapshot.WindowDpi,
            snapshot.AdvancedColor.RawFlags,
            snapshot.AdvancedColor.Supported,
            snapshot.AdvancedColor.Active,
            snapshot.AdvancedColor.HdrSupported,
            snapshot.AdvancedColor.HdrUserEnabled,
            snapshot.AdvancedColor.WideColorSupported,
            snapshot.AdvancedColor.WideColorUserEnabled,
            snapshot.AdvancedColor.Mode,
            snapshot.AdvancedColor.ColorEncoding,
            snapshot.AdvancedColor.BitsPerColorChannel,
            snapshot.SdrWhiteRaw,
            snapshot.SdrWhiteNits,
            snapshot.SdrWhiteFailureReason,
            profileStatus,
            profile?.Scope,
            profile?.FileName,
            profile?.Identity.Sha256Hex,
            contract.VisibleWarning,
            contract.VisibleWarning is null,
            true);
    }
}
