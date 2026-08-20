using System.Collections.Concurrent;

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
            try { return CubeLut.Load(p); }
            catch { return null; }
        });
    }

    /// <summary>
    /// Loads <paramref name="path"/> for the UI, letting the failure through so it can be shown.
    /// Call this when the user PICKS a file; the render path calls <see cref="Resolve"/>.
    /// </summary>
    public static CubeLut Validate(string path)
    {
        var lut = CubeLut.Load(path);
        Cache[path] = lut;
        return lut;
    }

    /// <summary>Drops a cached entry so an edited file is picked up again.</summary>
    public static void Forget(string path) => Cache.TryRemove(path, out _);
}
