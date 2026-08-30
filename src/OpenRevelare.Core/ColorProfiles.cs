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

    public static ColorProfileRef LinearAcesCg(ProfileRole role = ProfileRole.Working) =>
        For(ColorSpaces.AcesCg, role);

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
        if (space.Name.Equals(ColorSpaces.AcesCg.Name, StringComparison.OrdinalIgnoreCase))
            return BuiltInProfileId.LinearAcesCg;
        throw new ArgumentException($"'{space.Name}' is not a built-in profile.", nameof(space));
    }
}
