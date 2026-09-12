using OpenRevelare.ColorManagement;
using OpenRevelare.Presentation;

namespace OpenRevelare.Presentation.MacOS;

/// <summary>
/// A manually refreshed macOS display contract. The composition root calls <see cref="Refresh"/>
/// on <c>NSWindowDidChangeScreenNotification</c> / <c>NSWindowDidChangeBackingPropertiesNotification</c>
/// / <c>NSApplicationDidChangeScreenParametersNotification</c>; this assembly deliberately owns no
/// AppKit observer of its own, mirroring the Win32 environment's no-message-hook rule.
/// </summary>
public interface IMacOSDisplayEnvironment : IDisplayEnvironment, IDisposable
{
    MacOSDisplayDiagnostics Diagnostics { get; }
    bool Refresh();
}

/// <summary>
/// Lets a presenter hold the contract stable across its final validation and native call, the
/// same closed race the Win32 environment closes with its refresh lease.
/// </summary>
internal interface IMacOSDisplayContractLeaseProvider
{
    void WithStableContract(Action<DisplayContract> action);
}

public sealed class MacOSDisplayEnvironment :
    IMacOSDisplayEnvironment,
    IMacOSDisplayContractLeaseProvider
{
    private readonly object _stateGate = new();
    private readonly object _refreshGate = new();
    private readonly nint _viewHandle;
    private readonly IMacOSDisplayProbe _probe;
    private DisplayContract _current;
    private MacOSDisplayDiagnostics _diagnostics;
    private MacOSDisplaySemanticKey _semanticKey;
    private long _revision;
    private bool _disposed;

    /// <param name="viewHandle">The <c>NSView*</c> the preview lives in; its window's screen is probed.</param>
    public MacOSDisplayEnvironment(nint viewHandle)
        : this(viewHandle, Native.MacOSNativeDisplayProbe.Instance)
    {
    }

    internal MacOSDisplayEnvironment(nint viewHandle, IMacOSDisplayProbe probe)
    {
        ArgumentNullException.ThrowIfNull(probe);
        _viewHandle = viewHandle;
        _probe = probe;

        MacOSDisplayProbeSnapshot initial = SafeProbe();
        _revision = 1;
        _semanticKey = initial.SemanticKey;
        _current = MacOSDisplayContractProvider.Build(initial, _revision);
        _diagnostics = MacOSDisplayContractProvider.Diagnostics(initial, _current);
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

    public MacOSDisplayDiagnostics Diagnostics
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

    void IMacOSDisplayContractLeaseProvider.WithStableContract(Action<DisplayContract> action)
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
            MacOSDisplayProbeSnapshot next = SafeProbe();
            lock (_stateGate)
            {
                ThrowIfDisposed();
                if (next.SemanticKey == _semanticKey)
                {
                    _diagnostics = MacOSDisplayContractProvider.Diagnostics(next, _current);
                    return false;
                }

                if (_revision == long.MaxValue)
                    throw new InvalidOperationException("macOS display contract revision overflowed.");
                _revision++;
                _semanticKey = next.SemanticKey;
                _current = MacOSDisplayContractProvider.Build(next, _revision);
                _diagnostics = MacOSDisplayContractProvider.Diagnostics(next, _current);
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

    private MacOSDisplayProbeSnapshot SafeProbe()
    {
        try
        {
            return _probe.Probe(_viewHandle);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return MacOSDisplayProbeSnapshot.Failed(
                _viewHandle, $"macOS display probe failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}

/// <summary>
/// The macOS presentation policy (D-026), kept apart from the environment so it is testable as a
/// pure function of a snapshot.
/// </summary>
internal static class MacOSDisplayContractProvider
{
    /// <summary>
    /// ONE MODE, ALWAYS SYSTEM-OWNED. Unlike Windows there is no legacy/app-managed branch: the
    /// window server's ColorSync transform always runs (§8.1), so the only correct move is to hand
    /// it a tagged extended-linear-sRGB surface and let it do the last hop (D-006). The only other
    /// state is "we could not find out what screen this is", which is an emergency with a warning,
    /// never a guess.
    ///
    /// <para>
    /// REFERENCE WHITE IS NOT SCALED ON THIS PLATFORM. In Apple's carrier canonical <c>1.0</c> IS
    /// SDR white — brightness-relative, defined by the OS — so the scale is exactly one in every
    /// state. This is the mirror image of Windows HDR (D-020), where <c>1.0</c> is 80 nits and the
    /// application must lift diffuse white itself. Same carrier bytes, opposite ownership of the
    /// lift; the contract records which so PresentationBufferBuilder does the right thing on each.
    /// </para>
    ///
    /// <para>
    /// HEADROOM IS THE EDR VALUE, VERBATIM. <c>maximumExtendedDynamicRangeColorComponentValue</c>
    /// already answers "how far above SDR white can this panel go right now", including the effect
    /// of the user's brightness setting; it is one on an SDR-only display and on an EDR display
    /// whose headroom is currently exhausted. It is not clamped upward to the potential value:
    /// the potential is what the panel could do at another brightness, not what it will show.
    /// </para>
    /// </summary>
    internal static DisplayContract Build(MacOSDisplayProbeSnapshot snapshot, long revision)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        if (!snapshot.IsReliable)
        {
            return new DisplayContract(
                snapshot.DisplayId,
                revision,
                PresentationEncoding.UnmanagedEmergencySrgb8,
                FinalTransformOwner.None,
                null,
                DisplayContract.CanonicalNominalWhiteNits,
                1f,
                1f,
                "macOS unmanaged emergency",
                snapshot.FailureReason ??
                    "The screen behind the preview could not be identified; presenting untagged sRGB.");
        }

        float headroom = ExtendedHeadroom(snapshot.MaximumEdrValue);
        string mode = headroom > 1f ? "EDR" : "SDR";
        return new DisplayContract(
            snapshot.DisplayId,
            revision,
            PresentationEncoding.LinearExtendedSrgbRgba16F,
            FinalTransformOwner.SystemCompositor,
            null,
            DisplayContract.CanonicalNominalWhiteNits,
            1f,
            headroom,
            $"macOS Core Animation · {mode} · {snapshot.ColorSpaceName ?? "unknown colour space"}");
    }

    /// <summary>Finite, at least one; anything else AppKit reports is treated as "no headroom".</summary>
    internal static float ExtendedHeadroom(double maximumEdrValue) =>
        double.IsFinite(maximumEdrValue) && maximumEdrValue > 1.0 ? (float)maximumEdrValue : 1f;

    /// <summary>
    /// Whether the presenter should ask Core Animation for extended-range content.
    ///
    /// <para>
    /// THE CANDIDATE ANSWER TO D-012, STATED SO THE SPIKE HAS SOMETHING TO CONFIRM OR REFUTE.
    /// Request EDR exactly when the contract carries headroom to spend. On an SDR-only screen, or
    /// an EDR screen with no current headroom, asking for it buys nothing and (per Apple's notes)
    /// may cost the system a tone-mapping pass; on a screen with headroom, not asking clips every
    /// value above one at the layer. D-012 remains Open until a real display shows whether SDR
    /// brightness shifts when the flag is on — this rule is what that test exercises.
    /// </para>
    /// </summary>
    internal static bool ShouldRequestExtendedRange(DisplayContract contract) =>
        contract.Encoding == PresentationEncoding.LinearExtendedSrgbRgba16F &&
        contract.ExtendedHeadroom > 1f;

    internal static MacOSDisplayDiagnostics Diagnostics(
        MacOSDisplayProbeSnapshot snapshot,
        DisplayContract contract) => new(
        snapshot.DisplayId,
        contract.Revision,
        snapshot.DirectDisplayId,
        snapshot.LocalizedName,
        snapshot.ColorSpaceName,
        snapshot.BackingScaleFactor,
        snapshot.MaximumEdrValue,
        snapshot.MaximumPotentialEdrValue,
        snapshot.MaximumReferenceEdrValue,
        ShouldRequestExtendedRange(contract),
        contract.VisibleWarning,
        contract.VisibleWarning is null);
}
