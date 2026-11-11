using BitMiracle.LibTiff.Classic;
using OpenRevelare.Core;

namespace OpenRevelare.Gui.Services;

// Preserve the detector's encoded RGB numbers and ICC; no decode colour conversion.
internal static class DustTrainingSnapshot
{
    internal sealed record Checks(bool SameSizeAsExport, int ChangedOutsideMask, bool Usable);

    public static Checks WriteAndCheck(string source, string destination, DustMask mask, CancellationToken ct)
    {
        using var input = Tiff.Open(source, "r") ?? throw new IOException("Cannot read detector snapshot.");
        if (input.GetField(TiffTag.IMAGEWIDTH)[0].ToInt() != mask.Width ||
            input.GetField(TiffTag.IMAGELENGTH)[0].ToInt() != mask.Height ||
            input.GetField(TiffTag.BITSPERSAMPLE)[0].ToInt() != 16 ||
            input.GetField(TiffTag.SAMPLESPERPIXEL)[0].ToInt() != 3 ||
            input.GetField(TiffTag.PHOTOMETRIC)[0].ToInt() != (int)Photometric.RGB ||
            input.GetField(TiffTag.PLANARCONFIG)[0].ToInt() != (int)PlanarConfig.CONTIG)
            throw new InvalidDataException("Expected the original RGB16 detector grid.");
        var image = new ImageBuffer(mask.Width, mask.Height);
        var row = new byte[checked(mask.Width * 6)];
        for (int y = 0; y < mask.Height; y++)
        {
            ct.ThrowIfCancellationRequested();
            if (!input.ReadScanline(row, y)) throw new IOException("Cannot read detector row.");
            for (int i = 0; i < mask.Width * 3; i++) image.Data[y * mask.Width * 3 + i] = BitConverter.ToUInt16(row, i * 2) / 65535f;
        }
        mask.RelativeTo(mask.Bounds).Repair(image, mask.Width, mask.Height, cancellationToken: ct);
        using (var output = Tiff.Open(destination, "w") ?? throw new IOException("Cannot write repair snapshot."))
        {
            output.SetField(TiffTag.IMAGEWIDTH, mask.Width);
            output.SetField(TiffTag.IMAGELENGTH, mask.Height);
            output.SetField(TiffTag.BITSPERSAMPLE, 16);
            output.SetField(TiffTag.SAMPLESPERPIXEL, 3);
            output.SetField(TiffTag.PHOTOMETRIC, Photometric.RGB);
            output.SetField(TiffTag.PLANARCONFIG, PlanarConfig.CONTIG);
            output.SetField(TiffTag.ORIENTATION, Orientation.TOPLEFT);
            output.SetField(TiffTag.COMPRESSION, Compression.LZW);
            output.SetField(TiffTag.ROWSPERSTRIP, output.DefaultStripSize(0));
            if (TiffIO.ReadIccBytes(input) is { } icc) output.SetField(TiffTag.ICCPROFILE, icc.Length, icc);
            for (int y = 0; y < mask.Height; y++)
            {
                ct.ThrowIfCancellationRequested();
                for (int i = 0; i < mask.Width * 3; i++)
                    BitConverter.TryWriteBytes(row.AsSpan(i * 2, 2), (ushort)Math.Clamp(
                        MathF.Round(image.Data[y * mask.Width * 3 + i] * 65535), 0, 65535));
                if (!output.WriteScanline(row, y)) throw new IOException("Cannot write repair row.");
            }
        }
        using var check = Tiff.Open(destination, "r") ?? throw new IOException("Cannot verify repair snapshot.");
        bool sameSize = check.GetField(TiffTag.IMAGEWIDTH)[0].ToInt() == mask.Width &&
            check.GetField(TiffTag.IMAGELENGTH)[0].ToInt() == mask.Height;
        var actual = new byte[row.Length];
        var rows = mask.EnumerateRuns().ToLookup(r => r.Y);
        var selected = new bool[mask.Width];
        int changedOutside = 0;
        for (int y = 0; y < mask.Height; y++)
        {
            ct.ThrowIfCancellationRequested();
            if (!input.ReadScanline(row, y) || !check.ReadScanline(actual, y)) throw new IOException("Cannot verify repair row.");
            Array.Clear(selected);
            foreach (var run in rows[y]) Array.Fill(selected, true, run.Start, run.End - run.Start);
            for (int x = 0; x < mask.Width; x++)
                if (!selected[x] && !row.AsSpan(x * 6, 6).SequenceEqual(actual.AsSpan(x * 6, 6))) changedOutside++;
        }
        return new(sameSize, changedOutside, sameSize && changedOutside == 0);
    }
}
