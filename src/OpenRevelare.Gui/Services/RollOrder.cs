namespace OpenRevelare.Gui.Services;

/// <summary>What the 图库 wall is ordered by.</summary>
public enum RollSortKey
{
    /// <summary>When the roll was imported. The default, and the only order in which a roll never
    /// moves: every other key can change under an edit, and a wall that rearranges itself is one
    /// the user cannot navigate from memory.</summary>
    ImportedAt,
    ModifiedAt,
    LastOpenedAt,
    Title,
    RollNumber,
    DevDate,
}

/// <summary>
/// Ordering for the roll wall.
///
/// Free of Avalonia and of the view model on purpose: the interesting part is what happens to the
/// fields the user has not filled in and to a hand-typed date, and that is worth testing directly.
///
/// TWO RULES hold for every key:
///
/// 1. A roll with NO value for the key sorts last, in both directions. Reversing is a statement
///    about the rolls that have a value; "blank first" is never what anyone asked for. Measured on
///    a real library: 32 of 36 rolls have no 卷号 and 34 have no 冲洗日期, so without this rule
///    picking either key shows a screen of blank cards before the first roll that answers.
/// 2. Ties break on import time (newest first) and then on Id, so the order is total — two rolls
///    that compare equal must not swap places between two runs of the same sort.
/// </summary>
public static class RollOrder
{
    /// <summary>The keys in the order the picker lists them, with their untranslated labels.
    /// One table: the enum, the UI and anything persisted all read from here.</summary>
    public static readonly IReadOnlyList<(RollSortKey Key, string Label)> Keys = new[]
    {
        (RollSortKey.ImportedAt, "添加时间"),
        (RollSortKey.ModifiedAt, "最近修改"),
        (RollSortKey.LastOpenedAt, "最近打开"),
        (RollSortKey.Title, "卷名"),
        (RollSortKey.RollNumber, "卷号"),
        (RollSortKey.DevDate, "冲洗日期"),
    };

    /// <summary>Parse a persisted key name; unknown (or absent) falls back to the default.</summary>
    public static RollSortKey Parse(string? name) =>
        Enum.TryParse(name, ignoreCase: true, out RollSortKey key) ? key : RollSortKey.ImportedAt;

    public static List<Catalog.Roll> Sort(IEnumerable<Catalog.Roll> rolls, RollSortKey key, bool descending)
    {
        var list = rolls.ToList();
        list.Sort((a, b) => Compare(a, b, key, descending));
        return list;
    }

    public static int Compare(Catalog.Roll a, Catalog.Roll b, RollSortKey key, bool descending)
    {
        if (ReferenceEquals(a, b)) return 0;

        int c = key switch
        {
            RollSortKey.ImportedAt => Signed(a.ImportedAt.CompareTo(b.ImportedAt), descending),
            RollSortKey.ModifiedAt => Signed(a.ModifiedAt.CompareTo(b.ModifiedAt), descending),
            RollSortKey.LastOpenedAt => Signed(a.LastOpenedAt.CompareTo(b.LastOpenedAt), descending),
            RollSortKey.Title => CompareText(a.Title, b.Title, descending),
            RollSortKey.RollNumber => CompareText(a.RollNumber, b.RollNumber, descending),
            RollSortKey.DevDate => CompareDate(a.DevDate, b.DevDate, descending),
            _ => 0,
        };
        if (c != 0) return c;

        // Total order, so the wall never reshuffles between two identical sorts.
        c = b.ImportedAt.CompareTo(a.ImportedAt);
        return c != 0 ? c : string.CompareOrdinal(a.Id, b.Id);
    }

    private static int Signed(int c, bool descending) => descending ? -c : c;

    /// <summary>Natural order (so 卷号 2 precedes 12), blanks last either way.</summary>
    private static int CompareText(string? a, string? b, bool descending)
    {
        string x = (a ?? "").Trim(), y = (b ?? "").Trim();
        if (x.Length == 0 || y.Length == 0) return BlankLast(x.Length, y.Length);
        return Signed(NaturalOrder.CompareNames(x, y), descending);
    }

    /// <summary>
    /// 冲洗日期 is a free-text field — the user types 2025.09, 2025-09-12, 2025年9月, whatever
    /// their notebook says — so this reads a date out of it where it can and falls back where it
    /// cannot. Three tiers, in this order: dates that parsed (chronological), text that did not
    /// (natural order, so at least it groups), then blanks. A roll dated "洗坏了重冲" therefore
    /// sits after every real date instead of landing in the middle of 2019.
    /// </summary>
    private static int CompareDate(string? a, string? b, bool descending)
    {
        string x = (a ?? "").Trim(), y = (b ?? "").Trim();
        if (x.Length == 0 || y.Length == 0) return BlankLast(x.Length, y.Length);

        (int Year, int Month, int Day)? dx = ParseLooseDate(x), dy = ParseLooseDate(y);
        if (dx is null != dy is null) return dx is null ? 1 : -1;   // unparsed after parsed, always
        if (dx is { } px && dy is { } py)
        {
            int c = px.Year.CompareTo(py.Year);
            if (c == 0) c = px.Month.CompareTo(py.Month);
            if (c == 0) c = px.Day.CompareTo(py.Day);
            // Same date written two ways ("2025.09" vs "2025-9") still needs an order.
            if (c == 0) c = NaturalOrder.CompareNames(x, y);
            return Signed(c, descending);
        }
        return Signed(NaturalOrder.CompareNames(x, y), descending);
    }

    /// <summary>Blank sorts last regardless of direction. Returns 0 when both are blank, leaving
    /// the tie-break to decide.</summary>
    private static int BlankLast(int lenA, int lenB) =>
        lenA == lenB ? 0 : lenA == 0 ? 1 : -1;

    /// <summary>
    /// A year (and optionally a month and a day) out of a hand-typed date, or null.
    ///
    /// Works off the digit runs rather than a format list, which is what lets one implementation
    /// take 2025.09, 2025-09-12, 2025/9/1, 2025年9月 and 20250912 — the separators differ per
    /// person and per entry, but "the numbers, in order, starting with a four-digit year" holds
    /// for all of them. A missing month or day counts as 0, so 2025 precedes 2025.01.
    /// </summary>
    internal static (int Year, int Month, int Day)? ParseLooseDate(string text)
    {
        var runs = new List<string>();
        for (int i = 0; i < text.Length;)
        {
            if (!char.IsDigit(text[i])) { i++; continue; }
            int start = i;
            while (i < text.Length && char.IsDigit(text[i])) i++;
            runs.Add(text[start..i]);
        }
        if (runs.Count == 0) return null;

        // One long run is a compact date: 20250912 or 202509.
        if (runs.Count == 1 && runs[0].Length is 6 or 8)
        {
            string s = runs[0];
            return Build(Num(s[..4]), Num(s.Substring(4, 2)), s.Length == 8 ? Num(s.Substring(6, 2)) : 0);
        }

        if (runs[0].Length != 4) return null;   // no year to anchor on — not a date
        int year = Num(runs[0]);
        int month = runs.Count > 1 ? Num(runs[1]) : 0;
        int day = runs.Count > 2 ? Num(runs[2]) : 0;
        return Build(year, month, day);

        static int Num(string s) => int.TryParse(s, out int v) ? v : -1;

        static (int, int, int)? Build(int year, int month, int day) =>
            year is >= 1800 and <= 2200 && month is >= 0 and <= 12 && day is >= 0 and <= 31
                ? (year, month, day)
                : null;
    }
}
