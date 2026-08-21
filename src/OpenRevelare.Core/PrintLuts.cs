using System.Collections.Concurrent;
using System.Reflection;

namespace OpenRevelare.Core;

/// <summary>
/// Resolves <see cref="FrameParams.PrintLut"/> paths to loaded cubes, once each.
///
/// A cube is a few hundred kilobytes of text that parses into ~100k floats, and the render path
/// touches it for every frame, every preview and every thumbnail — reloading per call would put
/// file I/O and parsing inside the render loop. Cubes are immutable once loaded and
/// <see cref="CubeLut.Apply"/> only reads, so one instance is safely shared across the parallel
/// render.
///
/// A FAILED LOAD IS CACHED TOO, as a null. Without that, a missing or malformed file would be
/// retried on every frame of a roll — thousands of failed opens, and the same error surfaced
/// thousands of times. The user gets told once, by whoever chose the file.
/// </summary>
public static class PrintLuts
{
    private static readonly ConcurrentDictionary<string, CubeLut?> Cache = new();

    /// <summary>
    /// Print stocks shipped inside the assembly, keyed by the sentinel a project stores.
    ///
    /// WHY A SENTINEL AND NOT A PATH. A built-in has no stable path: it lives inside the
    /// assembly, and the assembly moves with the install. Storing one would make a project
    /// unopenable on another machine — exactly the failure the picker already works around for
    /// user cubes ("文件缺失"). A sentinel names the stock rather than a location, so a roll
    /// calibrated here renders the same anywhere the app is installed.
    ///
    /// The leading ':' cannot begin a real path on either platform, so a sentinel can never
    /// collide with a file the user picked, and older projects — which only ever stored real
    /// paths — are unaffected.
    /// </summary>
    public static readonly IReadOnlyList<(string Id, string Name)> Builtins = new[]
    {
        (":kodak-2383",      "Rec709 Kodak 2383 D65"),
        (":fujifilm-3513di", "Rec709 Fujifilm 3513DI D65"),
    };

    /// <summary>Whether <paramref name="path"/> names a built-in rather than a file on disk.</summary>
    public static bool IsBuiltin(string? path) =>
        !string.IsNullOrWhiteSpace(path) && path[0] == ':' && FindBuiltin(path) is not null;

    private static string? FindBuiltin(string path)
    {
        foreach ((string id, _) in Builtins)
            if (id.Equals(path, StringComparison.OrdinalIgnoreCase)) return id;
        return null;
    }

    /// <summary>
    /// Loads a built-in from the embedded resource named after its sentinel. Throws like
    /// <see cref="CubeLut.Load"/> does, so <see cref="Validate"/> can report a broken build the
    /// same way it reports a broken file.
    /// </summary>
    private static CubeLut LoadBuiltin(string id)
    {
        string name = $"OpenRevelare.Core.Assets.Luts.{id[1..]}.cube";
        var asm = typeof(PrintLuts).Assembly;
        using Stream? stream = asm.GetManifestResourceStream(name)
            ?? throw new InvalidDataException($"内置 LUT 缺失：{name}");
        using var reader = new StreamReader(stream);
        string fallback = Builtins.First(b => b.Id == id).Name;
        return CubeLut.Parse(reader, fallback);
    }

    /// <summary>
    /// The .cube files in <paramref name="dir"/>, sorted by name, as full paths.
    ///
    /// This is the drop-in folder: anything the user copies here shows up in the picker on the
    /// next start, without picking it file by file. It lives under the per-user data directory
    /// rather than beside the executable, because the install location is not writable on any of
    /// the three platforms — Program Files needs admin, a signed .app must not be modified, and
    /// an AppImage is a read-only mount whose path changes every run.
    ///
    /// NEVER THROWS. A missing folder is the normal case (nobody has added a LUT yet) and an
    /// unreadable one must not take the picker down with it: the built-ins and the recents are
    /// still perfectly usable, so a failure here returns nothing rather than propagating.
    /// Individual files are NOT parsed here — a folder of 50 cubes would mean 50 parses on every
    /// start, and the picker only needs the names until one is actually selected.
    /// </summary>
    public static IReadOnlyList<string> InFolder(string dir)
    {
        try
        {
            if (!Directory.Exists(dir)) return Array.Empty<string>();
            var files = Directory.GetFiles(dir, "*.cube", SearchOption.TopDirectoryOnly);
            Array.Sort(files, StringComparer.OrdinalIgnoreCase);
            return files;
        }
        catch { return Array.Empty<string>(); }
    }

    /// <summary>
    /// The cube for <paramref name="path"/>, or null when the path is empty (pass-through) or the
    /// file cannot be read.
    ///
    /// Never throws: the render path has no way to present an error, and a roll whose LUT went
    /// missing should still render — as a pass-through, which is what it did before the LUT was
    /// chosen — rather than fail to open. <see cref="Validate"/> is the call that reports why.
    /// </summary>
    public static CubeLut? Resolve(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        return Cache.GetOrAdd(path, static p =>
        {
            try { return IsBuiltin(p) ? LoadBuiltin(p) : CubeLut.Load(p); }
            catch { return null; }
        });
    }

    /// <summary>
    /// Loads <paramref name="path"/> for the UI, letting the failure through so it can be shown.
    /// Call this when the user PICKS a file; the render path calls <see cref="Resolve"/>.
    /// </summary>
    public static CubeLut Validate(string path)
    {
        var lut = IsBuiltin(path) ? LoadBuiltin(path) : CubeLut.Load(path);
        Cache[path] = lut;
        return lut;
    }

    /// <summary>Drops a cached entry so an edited file is picked up again.</summary>
    public static void Forget(string path) => Cache.TryRemove(path, out _);
}
