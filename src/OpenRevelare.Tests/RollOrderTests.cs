using OpenRevelare.Gui.Services;
using Xunit;

namespace OpenRevelare.Tests;

/// <summary>
/// The roll wall's ordering. The rules that need holding down are what happens to the fields the
/// user has not filled in (most of them, in a real library) and to a hand-typed date.
/// </summary>
public sealed class RollOrderTests
{
    private static Catalog.Roll Roll(
        string id,
        string title = "",
        string rollNumber = "",
        string devDate = "",
        int importedDaysAgo = 0)
        => new()
        {
            Id = id,
            Title = title,
            RollNumber = rollNumber,
            DevDate = devDate,
            ImportedAt = new DateTime(2026, 1, 1).AddDays(-importedDaysAgo),
        };

    private static string[] Order(IEnumerable<Catalog.Roll> rolls, RollSortKey key, bool descending)
        => RollOrder.Sort(rolls, key, descending).Select(r => r.Id).ToArray();

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_roll_with_no_value_sorts_last_in_both_directions(bool descending)
    {
        var rolls = new[]
        {
            Roll("blank"),
            Roll("b", rollNumber: "12"),
            Roll("a", rollNumber: "2"),
        };

        Assert.Equal("blank", Order(rolls, RollSortKey.RollNumber, descending)[^1]);
        Assert.Equal("blank", Order(rolls, RollSortKey.Title, descending)[^1]);
        Assert.Equal("blank", Order(rolls, RollSortKey.DevDate, descending)[^1]);
    }

    [Fact]
    public void Roll_numbers_sort_naturally_not_as_text()
    {
        var rolls = new[]
        {
            Roll("r12", rollNumber: "12"),
            Roll("r2", rollNumber: "2"),
            Roll("r9", rollNumber: "9"),
        };

        Assert.Equal(new[] { "r2", "r9", "r12" }, Order(rolls, RollSortKey.RollNumber, descending: false));
        Assert.Equal(new[] { "r12", "r9", "r2" }, Order(rolls, RollSortKey.RollNumber, descending: true));
    }

    /// <summary>Three tiers: real dates in order, then text that is not a date, then blanks.</summary>
    [Fact]
    public void Dev_dates_are_read_where_they_can_be_and_unparsed_text_falls_to_the_back()
    {
        var rolls = new[]
        {
            Roll("blank"),
            Roll("prose", devDate: "洗坏了重冲"),
            Roll("sep25", devDate: "2025.09"),
            Roll("jan19", devDate: "2019年1月8日"),
            Roll("compact", devDate: "20250101"),
        };

        Assert.Equal(new[] { "jan19", "compact", "sep25", "prose", "blank" },
                     Order(rolls, RollSortKey.DevDate, descending: false));
        // Reversing turns the DATES around; prose and blank keep their place at the back.
        Assert.Equal(new[] { "sep25", "compact", "jan19", "prose", "blank" },
                     Order(rolls, RollSortKey.DevDate, descending: true));
    }

    [Theory]
    [InlineData("2025.09", 2025, 9, 0)]
    [InlineData("2025-09-12", 2025, 9, 12)]
    [InlineData("2025/9/1", 2025, 9, 1)]
    [InlineData("2025年9月", 2025, 9, 0)]
    [InlineData("20250912", 2025, 9, 12)]
    [InlineData("202509", 2025, 9, 0)]
    [InlineData("2025", 2025, 0, 0)]
    [InlineData(" 2025.09.12 冲于柯达店 ", 2025, 9, 12)]
    public void Loose_dates_are_read_off_their_digit_runs(string text, int year, int month, int day)
        => Assert.Equal((year, month, day), RollOrder.ParseLooseDate(text));

    [Theory]
    [InlineData("洗坏了重冲")]
    [InlineData("")]
    [InlineData("九月")]
    [InlineData("9.2025")]     // no four-digit year to anchor on
    [InlineData("2025.13")]    // month out of range
    [InlineData("2025.09.55")] // day out of range
    public void Text_that_is_not_a_date_is_reported_as_such(string text)
        => Assert.Null(RollOrder.ParseLooseDate(text));

    /// <summary>A year alone precedes that year's January — "2025" is the least it can mean.</summary>
    [Fact]
    public void A_year_alone_precedes_its_own_january()
    {
        var rolls = new[] { Roll("jan", devDate: "2025.01"), Roll("year", devDate: "2025") };
        Assert.Equal(new[] { "year", "jan" }, Order(rolls, RollSortKey.DevDate, descending: false));
    }

    [Fact]
    public void Ties_break_on_import_time_so_the_order_is_total()
    {
        var rolls = new[]
        {
            Roll("old", title: "RAW", importedDaysAgo: 10),
            Roll("new", title: "RAW", importedDaysAgo: 1),
        };

        // Identical titles: newest import first, and the same either way round — the direction is
        // about the KEY, and a tie-break that flipped with it would reshuffle equal cards.
        Assert.Equal(new[] { "new", "old" }, Order(rolls, RollSortKey.Title, descending: false));
        Assert.Equal(new[] { "new", "old" }, Order(rolls, RollSortKey.Title, descending: true));
    }

    [Fact]
    public void Import_time_descending_is_the_default_and_the_persisted_name_round_trips()
    {
        Assert.Equal(RollSortKey.ImportedAt, RollOrder.Parse(null));
        Assert.Equal(RollSortKey.ImportedAt, RollOrder.Parse("nonsense"));
        Assert.Equal(RollSortKey.DevDate, RollOrder.Parse(nameof(RollSortKey.DevDate)));
        Assert.Equal(RollSortKey.LastOpenedAt, RollOrder.Parse("lastopenedat"));

        var rolls = new[] { Roll("old", importedDaysAgo: 10), Roll("new", importedDaysAgo: 1) };
        Assert.Equal(new[] { "new", "old" }, Order(rolls, RollSortKey.ImportedAt, descending: true));
        Assert.Equal(new[] { "old", "new" }, Order(rolls, RollSortKey.ImportedAt, descending: false));
    }

    /// <summary>Every key in the enum is offered, so adding one cannot leave it unreachable.</summary>
    [Fact]
    public void The_picker_lists_every_key()
        => Assert.Equal(Enum.GetValues<RollSortKey>(), RollOrder.Keys.Select(k => k.Key).ToArray());
}
