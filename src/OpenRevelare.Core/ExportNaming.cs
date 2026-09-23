namespace OpenRevelare.Core;

/// <summary>
/// The filename a batch export writes, from a template the user can state.
///
/// A roll export names its files after the SCANS by default, which is right for going back to the
/// original and wrong for delivery: "_DSC7659.tiff" says nothing about which roll or which film it
/// came from, and a folder holding three rolls' worth of them is a folder of anonymous numbers. The
/// roll already knows all of it — camera, film, developing date, roll number — so the template only
/// has to name what to spend.
///
/// Expansion is deliberately forgiving. A roll with no camera filled in is normal, so a token whose
/// field is empty leaves NO trace: the separators that would have surrounded it collapse, rather
/// than delivering "__0003" and a support question. What cannot be forgiving is the result — an
/// empty or filesystem-hostile stem would either fail the write or, worse, land somewhere
/// unexpected — so the last word belongs to <see cref="Sanitize"/> and the fallback chain in
/// <see cref="Expand"/>.
/// </summary>
public static class ExportNaming
{
    /// <summary>What a roll export did before templates existed: the scan's own name.</summary>
    public const string Default = "{Original}";

    /// <summary>
    /// What one frame can contribute to its own name. Everything except
    /// <see cref="Original"/> and <see cref="Sequence"/> comes from the roll, so it is the same for
    /// every frame in a batch.
    /// </summary>
    public readonly record struct Fields(
        string Roll,
        string RollNumber,
        string Camera,
        string Film,
        string Date,
        string Original,
        int Sequence);

    /// <summary>
    /// The tokens, in the order a UI should offer them: the two that distinguish frames first, then
    /// the roll's own fields.
    /// </summary>
    public static IReadOnlyList<string> Tokens { get; } =
        ["{Original}", "{Seq}", "{Roll}", "{RollNo}", "{Camera}", "{Film}", "{Date}"];

    /// <summary>
    /// The stem (no extension) for one frame. <paramref name="template"/> may be null or blank, in
    /// which case <see cref="Default"/> applies.
    ///
    /// <see cref="Fields.Sequence"/> is written as at least three digits so a roll sorts by name the
    /// way it sorts on the film — two digits would put frame 10 before frame 2 in every file
    /// manager, which is the whole reason to pad at all.
    /// </summary>
    public static string Expand(string? template, Fields fields)
    {
        string text = string.IsNullOrWhiteSpace(template) ? Default : template!;

        foreach (var (token, value) in new (string, string)[]
        {
            ("{Original}", fields.Original),
            ("{Seq}", fields.Sequence.ToString("D3")),
            ("{Roll}", fields.Roll),
            ("{RollNo}", fields.RollNumber),
            ("{Camera}", fields.Camera),
            ("{Film}", fields.Film),
            ("{Date}", fields.Date),
        })
        {
            // An empty field leaves a MARKER rather than a hole, so the separator the template put
            // around it can be removed with it below. Doing that here rather than by trimming the
            // finished name is what lets a scan called "_DSC7659" keep its leading underscore.
            text = Replace(text, token, string.IsNullOrEmpty(value) ? Hole : value);
        }

        string stem = Sanitize(CloseHoles(text));
        if (stem.Length > 0) return stem;

        // Nothing survived — every token the template named was empty, or the whole thing was
        // punctuation. The scan's own name is the only thing guaranteed to exist.
        string original = Sanitize(fields.Original ?? "");
        return original.Length > 0 ? original : $"frame-{Math.Max(1, fields.Sequence):D3}";
    }

    /// <summary>Case-insensitive token replacement: <c>{seq}</c> is the same request as
    /// <c>{Seq}</c>, and nobody should have to discover that by getting a literal brace in a
    /// filename.</summary>
    private static string Replace(string text, string token, string value)
        => text.Replace(token, value, StringComparison.OrdinalIgnoreCase);

    /// <summary>Stands in for a token whose field was empty, until <see cref="CloseHoles"/> removes
    /// it. A control character, so it cannot occur in a template someone typed.</summary>
    private const string Hole = "\u0001";

    /// <summary>
    /// Remove each empty token along with ONE separator that was there to attach it: the one before
    /// it, or — at the start of the name, where there is none — the one after. So
    /// <c>{Roll}_{Camera}_{Seq}</c> on a roll with no camera reads "roll_001", and
    /// <c>{Camera}_{Seq}</c> reads "001".
    /// </summary>
    private static string CloseHoles(string text)
    {
        var sb = new System.Text.StringBuilder(text.Length);
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] != Hole[0]) { sb.Append(text[i]); continue; }

            if (sb.Length > 0 && sb[^1] is '_' or '-' or ' ') sb.Length--;                // the one before
            else if (sb.Length == 0 && i + 1 < text.Length && text[i + 1] is '_' or '-' or ' ') i++;  // or after
        }
        return sb.ToString();
    }

    /// <summary>
    /// A filename stem the filesystem will accept: invalid characters become <c>_</c>, a repeated
    /// separator collapses, and the surrounding whitespace and trailing dots that Windows silently
    /// strips (and then disagrees with you about) are removed.
    ///
    /// It does NOT trim leading underscores: Nikon writes "_DSC7659" and the default template is
    /// the scan's own name, so trimming would rename every frame of an Adobe-RGB Nikon roll.
    ///
    /// Windows' reserved device names are suffixed rather than rejected — <c>NUL.tiff</c> cannot be
    /// created at all, and silently writing nothing is the one outcome an export must never have.
    /// </summary>
    public static string Sanitize(string name)
    {
        char[] bad = Path.GetInvalidFileNameChars();
        var sb = new System.Text.StringBuilder(name.Length);
        foreach (char c in name)
        {
            char mapped = Array.IndexOf(bad, c) >= 0 ? '_' : c;
            // Collapse a REPEATED separator, so "{Roll}_{Camera}_{Seq}" with no camera reads
            // "roll_003" rather than "roll__003". Only a repeat of the same character: a template
            // that deliberately writes " - " means it, and must survive.
            if (mapped is '_' or '-' or ' ' && sb.Length > 0 && sb[^1] == mapped) continue;
            sb.Append(mapped);
        }

        string stem = sb.ToString().Trim().TrimEnd('.');
        return IsReservedDeviceName(stem) ? stem + "_" : stem;
    }

    private static bool IsReservedDeviceName(string stem)
    {
        if (stem.Length is < 3 or > 4) return false;
        string upper = stem.ToUpperInvariant();
        if (upper is "CON" or "PRN" or "AUX" or "NUL") return true;
        return (upper.StartsWith("COM", StringComparison.Ordinal)
                || upper.StartsWith("LPT", StringComparison.Ordinal))
               && upper.Length == 4 && upper[3] is >= '1' and <= '9';
    }
}
