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
        : Missing ? "文件缺失"
        : $"{Roll.FrameCount} 帧 · {Roll.ModifiedAt:yyyy-MM-dd HH:mm}";

    /// <summary>Re-read the title/detail after a rename or a save.</summary>
    public void RefreshText()
    {
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(Subtitle));
        OnPropertyChanged(nameof(Detail));
        OnPropertyChanged(nameof(Missing));
    }
}

/// <summary>
/// The 图库 module: every roll this install knows about, as a wall of contact sheets.
///
/// Deliberately knows nothing about frames. Opening a roll hands its .ncproj to the editing view
/// model, which is the one thing that reads pixels — the library itself never decodes a negative,
/// it only reads one JPEG per roll. That is what keeps a 500-roll catalog scrollable.
/// </summary>
public sealed partial class LibraryViewModel : ObservableObject
{
    /// <summary>Cover width to decode at. The card is ~260 px wide; decoding straight to that
    /// (rather than full size then shrinking) is what makes a wall of covers cheap.</summary>
    private const int CoverWidth = 320;

    public ObservableCollection<RollCard> Rolls { get; } = new();

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

    /// <summary>Header count. Excludes the 新建 tile, which is furniture, not a roll.</summary>
    public string RollCountText => IsEmpty ? "还没有卷" : $"{Rolls.Count - 1} 卷";

    /// <summary>
    /// Reload from the catalog and decode the covers. Ordered by IMPORT time, newest first — so a
    /// roll keeps its place in the wall, and the roll you just made is the one next to the 新建
    /// tile. Sorting by last-opened instead would reshuffle the wall every time you looked at an
    /// old roll, which makes the layout impossible to remember.
    /// </summary>
    public async Task RefreshAsync()
    {
        foreach (RollCard c in Rolls) { c.Cover?.Dispose(); c.Cover = null; }
        Rolls.Clear();

        Rolls.Add(new RollCard(null));   // the 新建卷 tile always leads
        List<Catalog.Roll> rolls = Catalog.Rolls.OrderByDescending(r => r.ImportedAt).ToList();
        foreach (Catalog.Roll r in rolls) Rolls.Add(new RollCard(r));
        IsEmpty = rolls.Count == 0;
        Selected = Rolls.Skip(1).FirstOrDefault();

        // Covers land one at a time so the wall draws immediately and fills in, rather than
        // waiting on every JPEG before showing anything.
        foreach (RollCard card in Rolls.ToList()) await RefreshCoverAsync(card);
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
                live.RollNote = edited.RollNote;
            }
            else
            {
                Project.Data d = Project.Load(roll.ProjectPath);
                d.Meta.CameraBody = edited.CameraBody; d.Meta.FilmStock = edited.FilmStock;
                d.Meta.FilmIso = edited.FilmIso; d.Meta.RollNumber = edited.RollNumber;
                d.Meta.DevLab = edited.DevLab; d.Meta.DevProcess = edited.DevProcess;
                d.Meta.DevDate = edited.DevDate; d.Meta.Location = edited.Location;
                d.Meta.RollNote = edited.RollNote;
                Project.Save(roll.ProjectPath, d);
                // The cover still carries the OLD info bar; it is redrawn when this roll is next
                // opened. Re-rendering it here would mean decoding the whole roll from the wall.
            }

            roll.CameraBody = edited.CameraBody; roll.FilmStock = edited.FilmStock;
            roll.RollNumber = edited.RollNumber; roll.DevDate = edited.DevDate;
            Catalog.Upsert(roll);
            card.RefreshText();
            return null;
        }
        catch (Exception ex) { return "保存卷信息失败：" + ex.Message; }
    }

    /// <summary>Rename a roll: the catalog title and the project file's name, nothing else. The
    /// source images keep their names, and so does the roll NUMBER — that one is printed on the
    /// contact sheet and means something to the user.</summary>
    public string? Rename(RollCard card, string newTitle)
    {
        if (card.Roll is not { } roll) return null;
        string clean = Catalog.Sanitize(newTitle).Trim();
        if (clean.Length == 0) return "卷名不能为空";
        if (clean == roll.Title) return null;

        try
        {
            Catalog.Rename(roll, clean);
            card.RefreshText();
            return null;
        }
        catch (Exception ex) { return "重命名失败：" + ex.Message; }
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
