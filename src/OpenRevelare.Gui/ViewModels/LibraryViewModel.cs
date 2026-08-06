using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using OpenRevelare.Core;
using OpenRevelare.Gui.Models;
using OpenRevelare.Gui.Services;

namespace OpenRevelare.Gui.ViewModels;

/// <summary>
/// One roll as the library shows it: its catalog entry, plus the cover decoded off disk.
///
/// A wrapper rather than properties on <see cref="Catalog.Roll"/> itself, because that type is
/// what gets serialised — putting a Bitmap and change notification in it would drag Avalonia into
/// the on-disk model.
/// </summary>
public sealed partial class RollCard : ObservableObject
{
    /// <summary>Null on the 新建卷 tile — the one card that is a button, not a roll.</summary>
    public Catalog.Roll? Roll { get; }

    /// <summary>This is the leading 「+ 新建卷」 tile. It rides in the same collection as the
    /// rolls so it flows with them in the wrap: an entry outside the items panel would sit in its
    /// own row and stop being the first cell.</summary>
    public bool IsNewTile => Roll is null;

    public RollCard(Catalog.Roll? roll) => Roll = roll;

    [ObservableProperty] private Bitmap? _cover;

    [ObservableProperty] private bool _isSelected;

    /// <summary>No cover on disk yet (never opened since covers existed, or evicted). The card
    /// draws its title on a blank frame instead — a roll with no cover is still a roll.</summary>
    public bool HasNoCover => Cover is null;

    partial void OnCoverChanged(Bitmap? value) => OnPropertyChanged(nameof(HasNoCover));

    public string Title => Roll?.Title ?? "";
    public string Subtitle => Roll?.Subtitle ?? "";
    public bool Missing => Roll?.Missing ?? false;

    public string Detail => Roll is null ? ""
        : Missing ? Loc.T("文件缺失")
        : Loc.F($"{Roll.FrameCount} 帧 · {Roll.ModifiedAt:yyyy-MM-dd HH:mm}");

    /// <summary>Re-read the title/detail after a rename or a save.</summary>
    public void RefreshText()
    {
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(Subtitle));
        OnPropertyChanged(nameof(Detail));
        OnPropertyChanged(nameof(Missing));
    }
}

/// <summary>One value a roll can have inside a facet, with how many rolls have it.</summary>
public sealed partial class FilterOption : ObservableObject
{
    public string Value { get; }
    public int Count { get; }

    /// <summary>Value and tally in one string: a facet whose counts are invisible makes the user
    /// click through empty-ish filters to find out how much is behind each.</summary>
    public string Label => $"{Value}    {Count}";

    private readonly Action _onToggled;

    [ObservableProperty] private bool _isChecked;

    partial void OnIsCheckedChanged(bool value) => _onToggled();

    public FilterOption(string value, int count, Action onToggled)
    {
        Value = value;
        Count = count;
        _onToggled = onToggled;
    }
}

/// <summary>One filterable column of the catalog — 胶卷, 相机, 年份.</summary>
public sealed partial class FilterFacet : ObservableObject
{
    /// <summary>
    /// The Chinese source string, and this facet's identity: which ticks to restore across a
    /// rebuild is decided by it, and so is the 年份 ordering special case.
    ///
    /// Identity has to be the untranslated string. Keyed on <see cref="Name"/> instead, a facet
    /// would stop matching its own ticks the instant the user switched language — the heading
    /// would still read 胶卷 while the ticks it was holding were filed under "Film".
    /// </summary>
    public string Key { get; }

    /// <summary>Heading as shown. Resolved on read, not stored, so
    /// <see cref="LibraryViewModel"/> only has to re-raise it on a language switch.</summary>
    public string Name => Loc.T(Key);

    /// <summary>Which field of the roll's annotation this facet groups by. Carried on the facet
    /// rather than switched on its name, so adding one is a single line in the table below and
    /// never a second place to edit.</summary>
    internal Func<Catalog.Roll, string> Selector { get; }

    public ObservableCollection<FilterOption> Options { get; } = new();

    public FilterFacet(string key, Func<Catalog.Roll, string> selector)
    {
        Key = key;
        Selector = selector;
    }

    public void RefreshText() => OnPropertyChanged(nameof(Name));
}

/// <summary>
/// The 图库 module: every roll this install knows about, as a wall of contact sheets.
///
/// Deliberately knows nothing about frames. Opening a roll hands its .ncproj to the editing view
/// model, which is the one thing that reads pixels — the library itself never decodes a negative,
/// it only reads one JPEG per roll. That is what keeps a 500-roll catalog scrollable.
///
/// Filtering is done over the in-memory catalog, not by querying anything. <see cref="Catalog"/>
/// is already fully loaded — it exists so the roll list can be drawn without opening dozens of
/// project files — so faceting it is a LINQ pass over a few hundred small objects. (NexFilm, which
/// has the same sidebar, does exactly this too: its SQLite is the persistence layer, and the
/// filtering happens on a JavaScript array it loaded at startup.)
/// </summary>
public sealed partial class LibraryViewModel : ObservableObject
{
    /// <summary>Cover width to decode at. The card is ~260 px wide; decoding straight to that
    /// (rather than full size then shrinking) is what makes a wall of covers cheap.</summary>
    private const int CoverWidth = 320;

    /// <summary>
    /// Re-resolve the wall's text after a language switch.
    ///
    /// XAML looks after itself — <see cref="Markup.TExtension"/> binds rather than resolves, so
    /// every literal in LibraryView.axaml repaints on its own. What does not is the text a C#
    /// expression produced: the counts, the facet headings and each card's frame tally all came
    /// out of Loc.T/Loc.F once and stayed. Left alone they are what makes a switch land on a
    /// half-translated wall — 图库 next to "1 rolls", 胶卷 above cards reading "5 frames".
    ///
    /// The subscription is never unhooked because there is nothing to unhook it from: one of
    /// these exists per run, owned by <see cref="MainViewModel.Library"/> for the app's lifetime.
    /// </summary>
    public LibraryViewModel() => Loc.Changed += RetranslateText;

    private void RetranslateText()
    {
        OnPropertyChanged(nameof(FilterSummary));
        OnPropertyChanged(nameof(RollCountText));
        foreach (FilterFacet f in Facets) f.RefreshText();
        // _all, not Rolls: a card the filter is hiding right now still has to be right when the
        // filter is cleared, and re-raising a property on a card nobody is drawing costs nothing.
        foreach (RollCard c in _all) c.RefreshText();
    }

    /// <summary>What the wall currently SHOWS — the 新建 tile plus whatever survives the filter.</summary>
    public ObservableCollection<RollCard> Rolls { get; } = new();

    /// <summary>Every roll's card, filtered or not. Held separately so narrowing the filter is a
    /// list rebuild and never a cover re-decode: the cards keep the bitmaps they already own.</summary>
    private readonly List<RollCard> _all = new();

    public ObservableCollection<FilterFacet> Facets { get; } = new();

    [ObservableProperty] private string _searchText = "";

    partial void OnSearchTextChanged(string value) => ApplyFilter();

    /// <summary>Set while facets are being rebuilt, so restoring the previous ticks does not fire
    /// one filter pass per checkbox.</summary>
    private bool _rebuilding;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FilterSummary))]
    private bool _hasActiveFilter;

    public string FilterSummary => HasActiveFilter ? Loc.F($"已筛选 · 共 {_all.Count} 卷") : Loc.F($"共 {_all.Count} 卷");

    [ObservableProperty] private RollCard? _selected;

    /// <summary>Selection is a property of the card, because that is what the template can style;
    /// the view model only keeps the two of them consistent.</summary>
    partial void OnSelectedChanged(RollCard? oldValue, RollCard? newValue)
    {
        if (oldValue is not null) oldValue.IsSelected = false;
        if (newValue is not null) newValue.IsSelected = true;
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RollCountText))]
    private bool _isEmpty = true;

    /// <summary>Header count. Excludes the 新建 tile, which is furniture, not a roll. Shows the
    /// filtered count against the total when a filter is on, so a short wall is never mistaken for
    /// a lost catalog.</summary>
    public string RollCountText => IsEmpty ? Loc.T("还没有卷")
        : HasActiveFilter ? Loc.F($"{Rolls.Count - 1} / {_all.Count} 卷")
        : Loc.F($"{_all.Count} 卷");

    /// <summary>
    /// Reload from the catalog and decode the covers. Ordered by IMPORT time, newest first — so a
    /// roll keeps its place in the wall, and the roll you just made is the one next to the 新建
    /// tile. Sorting by last-opened instead would reshuffle the wall every time you looked at an
    /// old roll, which makes the layout impossible to remember.
    /// </summary>
    public async Task RefreshAsync()
    {
        foreach (RollCard c in _all) { c.Cover?.Dispose(); c.Cover = null; }
        _all.Clear();

        List<Catalog.Roll> rolls = Catalog.Rolls.OrderByDescending(r => r.ImportedAt).ToList();
        foreach (Catalog.Roll r in rolls) _all.Add(new RollCard(r));
        IsEmpty = rolls.Count == 0;

        RebuildFacets(rolls);
        ApplyFilter();

        // Covers land one at a time so the wall draws immediately and fills in, rather than
        // waiting on every JPEG before showing anything. Driven off _all, not the filtered view:
        // a card the filter is hiding right now still needs its cover for when it comes back.
        foreach (RollCard card in _all.ToList()) await RefreshCoverAsync(card);
    }

    /// <summary>
    /// Recompute the facet lists from the catalog, preserving whatever was ticked. Values a roll
    /// leaves blank are skipped rather than bucketed under "未填"— an empty film-stock field means
    /// the user has not told us, not that these rolls belong together.
    /// </summary>
    private void RebuildFacets(IReadOnlyList<Catalog.Roll> rolls)
    {
        var ticked = Facets
            .SelectMany(f => f.Options.Where(o => o.IsChecked).Select(o => (f.Key, o.Value)))
            .ToHashSet();

        _rebuilding = true;
        Facets.Clear();
        // Every field of 卷信息 that GROUPS. 卷号 is deliberately absent — it is unique per roll,
        // so a facet built on it is a list of one-item buckets; the search box covers it instead.
        // A facet whose values are all blank adds nothing and is dropped below, so a user who
        // only ever fills in 胶卷 sees one facet rather than seven empty headings.
        //
        // Untranslated on purpose: these are keys, and FilterFacet.Name does the translating.
        foreach (var (key, selector) in new (string, Func<Catalog.Roll, string>)[]
                 {
                     ("画幅", r => r.Format),
                     ("胶卷", r => r.FilmStock),
                     ("相机", r => r.CameraBody),
                     ("冲洗店", r => r.DevLab),
                     ("年份", r => Year(r.DevDate)),
                 })
        {
            var facet = new FilterFacet(key, selector);
            IEnumerable<IGrouping<string, Catalog.Roll>> groups = rolls
                .Where(r => !string.IsNullOrWhiteSpace(selector(r)))
                .GroupBy(r => selector(r).Trim(), StringComparer.OrdinalIgnoreCase);
            // Commonest first, because that is the order someone scanning the list wants; 年份
            // is the exception — a chronological axis read out of order is just confusing.
            groups = key == "年份"
                ? groups.OrderByDescending(g => g.Key, StringComparer.Ordinal)
                : groups.OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.CurrentCulture);
            foreach (IGrouping<string, Catalog.Roll> g in groups)
                facet.Options.Add(new FilterOption(g.Key, g.Count(), ApplyFilter)
                {
                    IsChecked = ticked.Contains((key, g.Key)),
                });
            if (facet.Options.Count > 0) Facets.Add(facet);
        }
        _rebuilding = false;
    }

    /// <summary>The year a 冲洗日期 starts with, or empty when it is not dated. Kept lenient on
    /// purpose: the field is free text, so anything not beginning with four digits simply does not
    /// take part in the year facet rather than becoming a junk bucket.</summary>
    private static string Year(string devDate)
    {
        string s = (devDate ?? "").TrimStart();
        return s.Length >= 4 && s.Take(4).All(char.IsAsciiDigit) ? s[..4] : "";
    }

    /// <summary>Rebuild the visible wall from <see cref="_all"/> under the current facets and
    /// search text. Within a facet the ticks are OR; between facets they are AND — the reading
    /// that makes "Portra 400 + 年份 2024" mean what it looks like.</summary>
    private void ApplyFilter()
    {
        if (_rebuilding) return;

        RollCard? previous = Selected;
        Rolls.Clear();
        Rolls.Add(new RollCard(null));   // the 新建卷 tile always leads, filter or no filter
        foreach (RollCard card in _all.Where(Matches)) Rolls.Add(card);

        HasActiveFilter = !string.IsNullOrWhiteSpace(SearchText)
                          || Facets.Any(f => f.Options.Any(o => o.IsChecked));
        OnPropertyChanged(nameof(RollCountText));
        OnPropertyChanged(nameof(FilterSummary));

        // Keep the selection when it survived the filter; otherwise fall to the first roll so the
        // wall is never left pointing at a card it is no longer showing.
        Selected = previous is not null && Rolls.Contains(previous)
            ? previous
            : Rolls.Skip(1).FirstOrDefault();
    }

    private bool Matches(RollCard card)
    {
        if (card.Roll is not { } roll) return true;

        foreach (FilterFacet facet in Facets)
        {
            var picked = facet.Options.Where(o => o.IsChecked).Select(o => o.Value).ToList();
            if (picked.Count == 0) continue;   // an untouched facet constrains nothing
            string value = facet.Selector(roll).Trim();
            if (!picked.Contains(value, StringComparer.OrdinalIgnoreCase)) return false;
        }

        string query = SearchText.Trim();
        if (query.Length == 0) return true;
        // Search spans every annotation field, including the ones with no facet — 卷号 has no
        // sensible facet but is exactly the sort of thing someone types into a search box.
        return new[]
            {
                roll.Title, roll.RollNumber, roll.FilmStock, roll.CameraBody,
                roll.FilmIso, roll.DevLab, roll.DevProcess, roll.Location, roll.DevDate,
                roll.Format,
            }
            .Any(field => field?.Contains(query, StringComparison.CurrentCultureIgnoreCase) == true);
    }

    /// <summary>Drop every tick and the search box in one go — with three facets it is otherwise
    /// entirely possible to lose track of why the wall is short.</summary>
    public void ClearFilters()
    {
        _rebuilding = true;
        foreach (FilterOption option in Facets.SelectMany(f => f.Options)) option.IsChecked = false;
        _rebuilding = false;
        SearchText = "";   // setter runs ApplyFilter; do it last so one pass covers everything
        ApplyFilter();
    }

    /// <summary>(Re)read one card's cover off disk, decoding straight to card width.</summary>
    public async Task RefreshCoverAsync(RollCard card)
    {
        if (card.Roll is null) return;
        string path = SheetStore.PathFor(card.Roll.Id);
        if (!File.Exists(path)) return;
        try
        {
            Bitmap bmp = await Task.Run(() =>
            {
                using FileStream fs = File.OpenRead(path);
                return Bitmap.DecodeToWidth(fs, CoverWidth);
            });
            card.Cover?.Dispose();
            card.Cover = bmp;
        }
        catch { /* unreadable cover → card keeps its blank frame */ }
    }

    /// <summary>
    /// Hands back the LIVE notes object when the given roll is the one currently open for editing.
    /// Supplied by <see cref="MainViewModel"/>. Without it, editing an open roll's info would
    /// write the project file behind the editor's back and be overwritten by the next autosave.
    /// </summary>
    public Func<string, RollNotes?>? LiveNotesFor;

    /// <summary>Read a roll's annotation — from the running editor if that roll is open, else out
    /// of its project file.</summary>
    public RollNotes? LoadNotes(RollCard card)
    {
        if (card.Roll is not { } roll) return null;
        if (LiveNotesFor?.Invoke(roll.Id) is { } live) return live;
        try
        {
            Project.RollMeta m = Project.Load(roll.ProjectPath).Meta;
            return new RollNotes
            {
                CameraBody = m.CameraBody, FilmStock = m.FilmStock, FilmIso = m.FilmIso,
                RollNumber = m.RollNumber, DevLab = m.DevLab, DevProcess = m.DevProcess,
                DevDate = m.DevDate, Location = m.Location, RollNote = m.RollNote,
                Format = m.Format,
            };
        }
        catch { return null; }
    }

    /// <summary>Write a roll's annotation back, and refresh what the wall shows of it.</summary>
    public string? SaveNotes(RollCard card, RollNotes edited)
    {
        if (card.Roll is not { } roll) return null;
        try
        {
            if (LiveNotesFor?.Invoke(roll.Id) is { } live)
            {
                // The editor owns the file while a roll is open; changing its notes dirties the
                // roll, so the autosave writes them (and redraws the cover) on the next pause.
                live.CameraBody = edited.CameraBody; live.FilmStock = edited.FilmStock;
                live.FilmIso = edited.FilmIso; live.RollNumber = edited.RollNumber;
                live.DevLab = edited.DevLab; live.DevProcess = edited.DevProcess;
                live.DevDate = edited.DevDate; live.Location = edited.Location;
                live.RollNote = edited.RollNote; live.Format = edited.Format;
            }
            else
            {
                Project.Data d = Project.Load(roll.ProjectPath);
                d.Meta.CameraBody = edited.CameraBody; d.Meta.FilmStock = edited.FilmStock;
                d.Meta.FilmIso = edited.FilmIso; d.Meta.RollNumber = edited.RollNumber;
                d.Meta.DevLab = edited.DevLab; d.Meta.DevProcess = edited.DevProcess;
                d.Meta.DevDate = edited.DevDate; d.Meta.Location = edited.Location;
                d.Meta.RollNote = edited.RollNote; d.Meta.Format = edited.Format;
                Project.Save(roll.ProjectPath, d);
                // The cover still carries the OLD info bar; it is redrawn when this roll is next
                // opened. Re-rendering it here would mean decoding the whole roll from the wall.
            }

            roll.CameraBody = edited.CameraBody; roll.FilmStock = edited.FilmStock;
            roll.RollNumber = edited.RollNumber; roll.DevDate = edited.DevDate;
            roll.FilmIso = edited.FilmIso; roll.DevLab = edited.DevLab;
            roll.DevProcess = edited.DevProcess; roll.Location = edited.Location;
            roll.Format = edited.Format;
            Catalog.Upsert(roll);
            card.RefreshText();
            // Editing 卷信息 changes what the sidebar can offer, so the facets are recounted here —
            // otherwise a film stock just typed does not exist as a filter until the library is
            // reopened, which reads as the filter being broken.
            RebuildFacets(_all.Select(c => c.Roll).OfType<Catalog.Roll>().ToList());
            ApplyFilter();
            return null;
        }
        catch (Exception ex) { return Loc.T("保存卷信息失败：") + ex.Message; }
    }

    /// <summary>Rename a roll: the catalog title and the project file's name, nothing else. The
    /// source images keep their names, and so does the roll NUMBER — that one is printed on the
    /// contact sheet and means something to the user.</summary>
    public string? Rename(RollCard card, string newTitle)
    {
        if (card.Roll is not { } roll) return null;
        string clean = Catalog.Sanitize(newTitle).Trim();
        if (clean.Length == 0) return Loc.T("卷名不能为空");
        if (clean == roll.Title) return null;

        try
        {
            Catalog.Rename(roll, clean);
            card.RefreshText();
            return null;
        }
        catch (Exception ex) { return Loc.T("重命名失败：") + ex.Message; }
    }

    /// <summary>Forget a roll. <paramref name="deleteProject"/> also removes its .ncproj — i.e.
    /// the edit itself — which is why the caller confirms it separately.</summary>
    public void Remove(RollCard card, bool deleteProject)
    {
        if (card.Roll is not { } roll) return;
        if (deleteProject)
        {
            try { File.Delete(roll.ProjectPath); } catch { /* already gone */ }
        }
        SheetStore.Delete(roll.Id);
        Catalog.Remove(roll.Id);
        card.Cover?.Dispose();
        Rolls.Remove(card);
        IsEmpty = Rolls.Count <= 1;   // the 新建 tile does not count as content
    }
}
