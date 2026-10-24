namespace OpenRevelare.Gui.Models;

internal static class CropAspectPresets
{
    // Index-aligned with CropPresetCombo. Equivalent formats resolve to the first entry.
    internal static readonly double?[] Aspects =
    {
        null, 3.0 / 2, 37.0 / 35, 3.0 / 4, 65.0 / 24, 4.0 / 3, 1.0, 7.0 / 6, 3.0 / 2, 2.0 / 1,
    };

    internal static (int Index, double? Aspect) Match(double width, double height)
    {
        if (!double.IsFinite(width) || !double.IsFinite(height) || width <= 0 || height <= 0)
            return (0, null);
        double ratio = width / height, bestError = double.PositiveInfinity;
        (int Index, double? Aspect) best = (0, null);
        // Prefer a preset whose label already matches the orientation (4:3 over rotated 3:4).
        foreach (bool inverse in new[] { false, true })
        {
            for (int i = 1; i < Aspects.Length; i++)
            {
                double preset = Aspects[i]!.Value;
                double aspect = inverse ? 1 / preset : preset;
                double error = Math.Abs(Math.Log(ratio / aspect));
                // Allow the small ratio error caused by preview/source pixel rounding.
                if (error <= 0.002 && error < bestError)
                {
                    bestError = error;
                    best = (i, aspect);
                }
            }
        }
        return best;
    }
}
