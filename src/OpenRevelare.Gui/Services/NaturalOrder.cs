using System;
using System.Collections.Generic;

namespace OpenRevelare.Gui.Services;

/// <summary>
/// File-name ordering the way a photographer reads a roll: DSC_9.ARW before DSC_10.ARW.
///
/// Plain string ordering puts "10" before "9" because it compares digit by digit, which scrambles
/// any roll whose frame numbers cross a power of ten — the common case, since a 36-exposure roll
/// runs 1..36. Digit runs are therefore compared as numbers and everything else case-insensitively
/// as text.
///
/// Only the file NAME is compared, not the directory: a roll assembled from several folders is
/// still one roll, and the frame numbers are what the sequence means.
/// </summary>
public sealed class NaturalOrder : IComparer<string>
{
    public static readonly NaturalOrder Instance = new();

    /// <summary>Compare two full paths by their file names in natural order.</summary>
    public int Compare(string? a, string? b)
    {
        if (ReferenceEquals(a, b)) return 0;
        if (a is null) return -1;
        if (b is null) return 1;
        int c = CompareNames(System.IO.Path.GetFileName(a), System.IO.Path.GetFileName(b));
        // Same file name in two folders: fall back to the whole path so the order is still total
        // and stable (two frames must never compare equal, or a sort could interleave them).
        return c != 0 ? c : string.Compare(a, b, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Natural comparison of two bare file names.</summary>
    public static int CompareNames(string a, string b)
    {
        int i = 0, j = 0;
        while (i < a.Length && j < b.Length)
        {
            if (char.IsDigit(a[i]) && char.IsDigit(b[j]))
            {
                int si = i, sj = j;
                while (i < a.Length && char.IsDigit(a[i])) i++;
                while (j < b.Length && char.IsDigit(b[j])) j++;
                int c = CompareDigitRuns(a.AsSpan(si, i - si), b.AsSpan(sj, j - sj));
                if (c != 0) return c;
                continue;
            }
            char ca = char.ToUpperInvariant(a[i]);
            char cb = char.ToUpperInvariant(b[j]);
            if (ca != cb) return ca.CompareTo(cb);
            i++; j++;
        }
        return (a.Length - i).CompareTo(b.Length - j);
    }

    /// <summary>
    /// Compare two digit runs by value, without parsing: a scan named with a 30-digit serial would
    /// overflow every integer type, and the leading-zero rule below cannot be expressed as a number
    /// anyway.
    /// </summary>
    private static int CompareDigitRuns(ReadOnlySpan<char> a, ReadOnlySpan<char> b)
    {
        ReadOnlySpan<char> ta = a.TrimStart('0');
        ReadOnlySpan<char> tb = b.TrimStart('0');
        if (ta.Length != tb.Length) return ta.Length - tb.Length;   // more digits → larger number
        int c = ta.SequenceCompareTo(tb);
        if (c != 0) return c;
        // Equal value, different zero padding (IMG_007 vs IMG_7): order them by padding so the
        // comparison stays a strict ordering rather than declaring two different names equal.
        return a.Length - b.Length;
    }
}
