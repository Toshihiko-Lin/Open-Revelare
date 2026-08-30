using OpenRevelare.ColorManagement;

namespace OpenRevelare.Core;

/// <summary>Explicit profile policy at a typed export boundary.</summary>
public enum ExportProfilePolicy
{
    /// <summary>Embed the exact immutable ICC payload carried by the rendered pixels.</summary>
    EmbedExact,

    /// <summary>
    /// Deliberately omit an exact sRGB display profile. Other spaces are rejected because an
    /// untagged wide-gamut or scene-linear file is not portable enough to be an implicit option.
    /// </summary>
    OmitExactSrgb,
}

internal static class ExportColorPolicy
{
    internal static byte[]? ResolveProfileBytes(
        RenderedFrame frame,
        ExportProfilePolicy policy)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (!Enum.IsDefined(policy)) throw new ArgumentOutOfRangeException(nameof(policy));

        if (policy == ExportProfilePolicy.EmbedExact)
            return frame.OutputProfile.IccBytes.ToArray();

        ColorProfileRef exactSrgb = BuiltInColorProfiles.Srgb(ProfileRole.Output);
        bool isExactSrgbDisplay = frame.OutputProfile.Identity == exactSrgb.Identity
            && frame.Encoding.Reference == ColorReference.DisplayReferred
            && frame.Encoding.Transfer == TransferState.ProfileEncoded;
        if (!isExactSrgbDisplay)
        {
            throw new InvalidOperationException(
                $"ICC omission is permitted only for exact display-referred sRGB pixels; " +
                $"received {frame.OutputProfile.Description} [{frame.OutputProfile.Identity}].");
        }

        return null;
    }
}
