using System.Runtime.InteropServices;
using OpenRevelare.Core;

namespace OpenRevelare.Gui.Services;

internal static partial class PureNegComponent
{
    // Matches the source context used by the pinned film-dust large-scan healer.
    private const int RepairMargin = 192;
    private static readonly Lazy<string> RepairAdapter = new(() =>
    {
        using var resource = typeof(PureNegComponent).Assembly.GetManifestResourceStream("PureNegRepair")
            ?? throw new InvalidOperationException("PURENeg repair adapter is missing.");
        using var reader = new StreamReader(resource);
        return reader.ReadToEnd();
    });

    internal static void RegisterMaskRepair() =>
        DustMask.OptionalRepair = (() => IsInstalled, RepairMargin, RepairMask);

    private static void RepairMask(ImageBuffer image, IReadOnlyCollection<int> targets, CancellationToken ct)
    {
        int left = image.Width, top = image.Height, right = 0, bottom = 0;
        foreach (int pixel in targets)
        {
            ct.ThrowIfCancellationRequested();
            int x = pixel % image.Width, y = pixel / image.Width;
            left = Math.Min(left, x);
            top = Math.Min(top, y);
            right = Math.Max(right, x + 1);
            bottom = Math.Max(bottom, y + 1);
        }
        left = Math.Max(0, left - RepairMargin);
        top = Math.Max(0, top - RepairMargin);
        right = Math.Min(image.Width, right + RepairMargin);
        bottom = Math.Min(image.Height, bottom + RepairMargin);
        int width = right - left, height = bottom - top;

        // The healer uses unsigned 16-bit RGB. Preserve extended-range scanner values
        // with one common affine mapping and only write repaired samples back.
        double min = 0, max = 1;
        for (int y = top; y < bottom; y++)
        {
            ct.ThrowIfCancellationRequested();
            foreach (float value in image.Data.AsSpan((y * image.Width + left) * 3, width * 3))
            {
                if (!float.IsFinite(value)) continue;
                min = Math.Min(min, value);
                max = Math.Max(max, value);
            }
        }

        var workspace = Directory.CreateTempSubdirectory("openrevelare-pureneg-repair-");
        try
        {
            string input = Path.Combine(workspace.FullName, "rgb.u16");
            string selection = Path.Combine(workspace.FullName, "pixels.u32");
            string output = Path.Combine(workspace.FullName, "repair.u16");
            var row = new ushort[width * 3];
            double encode = ushort.MaxValue / (max - min);
            using (var stream = File.Create(input))
            {
                for (int y = top; y < bottom; y++)
                {
                    ct.ThrowIfCancellationRequested();
                    var source = image.Data.AsSpan((y * image.Width + left) * 3, row.Length);
                    for (int i = 0; i < row.Length; i++)
                        row[i] = float.IsFinite(source[i]) ? (ushort)Math.Round((source[i] - min) * encode) : (ushort)0;
                    stream.Write(MemoryMarshal.AsBytes(row.AsSpan()));
                }
            }

            using (var writer = new BinaryWriter(File.Create(selection)))
            {
                foreach (int pixel in targets)
                {
                    ct.ThrowIfCancellationRequested();
                    writer.Write((pixel / image.Width - top) * width + pixel % image.Width - left);
                }
            }

            RunAsync(Root, workspace.FullName, "result", null, ct, "--eval", RepairAdapter.Value, "--",
                Root, input, selection, output, width.ToString(System.Globalization.CultureInfo.InvariantCulture),
                height.ToString(System.Globalization.CultureInfo.InvariantCulture)).GetAwaiter().GetResult();

            byte[] repaired = File.ReadAllBytes(output);
            if (repaired.Length != targets.Count * 3 * sizeof(ushort))
                throw new InvalidDataException("Unexpected PURENeg repair output size.");
            ct.ThrowIfCancellationRequested();
            var values = MemoryMarshal.Cast<byte, ushort>(repaired.AsSpan());
            int next = 0;
            foreach (int pixel in targets)
            {
                for (int channel = 0; channel < 3; channel++)
                    image.Data[pixel * 3 + channel] = (float)(min + values[next++] / encode);
            }
        }
        finally
        {
            DeleteTemporaryDirectory(workspace.FullName);
        }
    }
}
