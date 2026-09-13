using System.Collections.Concurrent;
using OpenRevelare.ColorManagement;

namespace OpenRevelare.Core;

/// <summary>
/// Exact, immutable ICC identities for the colour spaces the legacy render already knows.
/// <see cref="ColorSpaceDef.Name"/> remains a project/UI identifier; these bytes are the profile
/// source of truth at typed colour boundaries.
/// </summary>
public static class BuiltInColorProfiles
{
    private static readonly ConcurrentDictionary<(string Name, ProfileRole Role), ColorProfileRef>
        Cache = new();

    public static ColorProfileRef For(ColorSpaceDef space, ProfileRole role) =>
        Cache.GetOrAdd((space.Name, role), _ => Create(space, role));

    public static ColorProfileRef Srgb(ProfileRole role = ProfileRole.Output) =>
        For(ColorSpaces.Srgb, role);

    public static ColorProfileRef DisplayP3(ProfileRole role = ProfileRole.Output) =>
        For(ColorSpaces.DisplayP3, role);

    public static ColorProfileRef AdobeRgb(ProfileRole role = ProfileRole.Output) =>
        For(ColorSpaces.AdobeRgb, role);

    public static ColorProfileRef Rec709(ProfileRole role = ProfileRole.Output) =>
        For(ColorSpaces.Rec709, role);

    /// <summary>The DCI cinema target a Resolve "DCI-P3, Gamma 2.6" film look renders to. Input side only in practice.</summary>
    public static ColorProfileRef DciP3(ProfileRole role = ProfileRole.Input) =>
        For(ColorSpaces.DciP3, role);

    public static ColorProfileRef LinearAcesCg(ProfileRole role = ProfileRole.Working) =>
        For(ColorSpaces.AcesCg, role);

    public static ColorProfileRef LinearExtendedSrgb(
        ProfileRole role = ProfileRole.CanonicalPresentation) =>
        For(ColorSpaces.LinearExtendedSrgb, role);

    /// <summary>
    /// Describes the pixels produced by the v1 print-LUT exit without changing them. The cube
    /// leaves a Rec709/2.4 curve in place, while wider output selections rotate only the
    /// primaries. M2 removes this compatibility profile by performing the required full colour
    /// conversion before constructing a rendered frame.
    /// </summary>
    public static ColorProfileRef LegacyPrintLutOutput(ColorSpaceDef selected) =>
        Cache.GetOrAdd(($"legacy-print-lut:{selected.Name}", ProfileRole.Output), _ =>
        {
            var actual = new ColorSpaceDef(
                $"Legacy-{selected.Name}-Rec709TRC",
                selected.Red,
                selected.Green,
                selected.Blue,
                selected.White,
                TransferFunction.Power,
                2.4);
            return ColorProfileRef.Create(
                IccProfiles.Build(actual),
                $"Legacy v1 {selected.Name} primaries / Rec709 2.4 TRC",
                ProfileRole.Output,
                new ProfileSource.Generated("OpenRevelare.IccProfiles", "legacy-v1"));
        });

    /// <summary>
    /// Truthfully describes the frozen v1 route used when DisplayReferredStage2 was disabled.
    /// That route never performed the working-to-output primary conversion (and ignored a print
    /// LUT entirely); it only applied the selected output transfer function to ACEScg-channel
    /// numbers. Keeping the old pixels therefore requires this compatibility profile rather than
    /// relabelling them as the requested output space.
    /// </summary>
    public static ColorProfileRef LegacyLinearStage2Output(ColorSpaceDef selected) =>
        Cache.GetOrAdd(($"legacy-linear-stage2:{selected.Name}", ProfileRole.Output), _ =>
        {
            ColorSpaceDef working = ColorSpaces.AcesCg;
            var actual = new ColorSpaceDef(
                $"Legacy-ACEScg-{selected.Name}-TRC",
                working.Red,
                working.Green,
                working.Blue,
                working.White,
                selected.Transfer,
                selected.Gamma);
            return ColorProfileRef.Create(
                IccProfiles.Build(actual),
                $"Legacy v1 ACEScg primaries / {selected.Name} transfer",
                ProfileRole.Output,
                new ProfileSource.Generated("OpenRevelare.IccProfiles", "legacy-v1"));
        });

    private static ColorProfileRef Create(ColorSpaceDef space, ProfileRole role) =>
        ColorProfileRef.Create(
            IccProfiles.Build(space),
            space.Name,
            role,
            new ProfileSource.BuiltIn(ToBuiltInId(space)));

    private static BuiltInProfileId ToBuiltInId(ColorSpaceDef space)
    {
        if (space.Name.Equals(ColorSpaces.Srgb.Name, StringComparison.OrdinalIgnoreCase))
            return BuiltInProfileId.Srgb;
        if (space.Name.Equals(ColorSpaces.DisplayP3.Name, StringComparison.OrdinalIgnoreCase))
            return BuiltInProfileId.DisplayP3;
        if (space.Name.Equals(ColorSpaces.AdobeRgb.Name, StringComparison.OrdinalIgnoreCase))
            return BuiltInProfileId.AdobeRgb1998;
        if (space.Name.Equals(ColorSpaces.Rec709.Name, StringComparison.OrdinalIgnoreCase))
            return BuiltInProfileId.Rec709;
        if (space.Name.Equals(ColorSpaces.DciP3.Name, StringComparison.OrdinalIgnoreCase))
            return BuiltInProfileId.DciP3;
        if (space.Name.Equals(ColorSpaces.AcesCg.Name, StringComparison.OrdinalIgnoreCase))
            return BuiltInProfileId.LinearAcesCg;
        if (space.Name.Equals(ColorSpaces.LinearExtendedSrgb.Name, StringComparison.OrdinalIgnoreCase))
            return BuiltInProfileId.LinearExtendedSrgb;
        throw new ArgumentException($"'{space.Name}' is not a built-in profile.", nameof(space));
    }
}
