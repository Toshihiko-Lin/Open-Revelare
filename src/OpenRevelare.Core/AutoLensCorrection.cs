namespace OpenRevelare.Core;

/// <summary>One exclusive automatic source: embedded parameters, Lensfun, or none.</summary>
public sealed record AutoLensCorrection(LensCorrectionEstimate? Embedded, LensfunCalibration? Lensfun, string Diagnostic = "")
{
    public static bool IsUsable(LensCorrectionEstimate? e) => e is { Native.IsUsable: true } || e is { IsHeuristic: false, Native: null }
        && (e.HasDistortion || e.HasVignette)
        && (!e.HasDistortion || (double.IsFinite(e.DistortionK1) && Math.Abs(e.DistortionK1) <= 0.5))
        && (!e.HasVignette || (double.IsFinite(e.VignetteAmount) && e.VignetteAmount > -1 && e.VignetteAmount <= 2));

    public static AutoLensCorrection Select(LensCorrectionEstimate? embedded, LensfunCalibration? lensfun) =>
        IsUsable(embedded) ? new(embedded, null) : new(null, lensfun is { IsUsable: true } ? lensfun : null, embedded?.Diagnostic ?? "未检测到内嵌校正数据");

    public static LensfunCalibration? MatchLensfun(string path)
    {
        try
        {
            var database = LensfunIdentificationDatabase.Bundled.Value;
            var capture = LensCaptureInfo.Read(path);
            var match = database.Identify(capture);
            return match.Profile is { } profile ? database.CreateCalibration(profile, capture) : null;
        }
        catch { return null; } // A bad metadata block must not prevent an uncorrected import.
    }

    public void Apply(FrameParams p, bool lcc)
    {
        // Re-running auto selection replaces the previous lens adjustments, including fine tuning.
        p.DistortionK1 = 0; p.VignetteAmount = 0; p.VignetteFalloff = 2.5;
        p.LensCorrectionDiagnostic = Diagnostic;
        p.EmbeddedLens = Embedded?.Native?.Copy();
        p.LensCorrectionOrigin = ""; p.LensCorrectionHasVignette = false;
        p.Lensfun = Lensfun ?? p.Lensfun;
        p.LensfunEnabled = Lensfun is not null;
        if (Embedded is not { } e) return;
        p.LensCorrectionOrigin = e.Summary;
        if (p.EmbeddedLens is not null) return;
        if (e.HasDistortion) p.DistortionK1 = e.DistortionK1;
        if (e.HasVignette && !lcc)
        {
            p.VignetteAmount = e.VignetteAmount; p.VignetteFalloff = e.VignetteFalloff;
            p.LensCorrectionHasVignette = true;
        }
    }
}
