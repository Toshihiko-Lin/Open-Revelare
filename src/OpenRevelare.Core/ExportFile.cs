namespace OpenRevelare.Core;

/// <summary>
/// Putting an export on disk without destroying what is already there.
///
/// Both halves exist because the naive form of each has a failure that only shows up on the
/// user's own files, after the fact:
///
/// • <see cref="Write"/> — <c>Tiff.Open(path, "w")</c> and <c>Image.Save(path)</c> truncate the
///   destination the moment they open it. A 60 MP TIFF that runs out of disk, or throws, or is
///   interrupted halfway therefore leaves a stump where the previous good export used to be.
///   Staging into a sibling temp file and renaming it over the target means the destination only
///   ever holds a complete file: either the old one or the new one, never a partial one.
///
/// • <see cref="Reserve"/> — a batch that writes straight to <c>folder/name.ext</c> silently
///   overwrites anything already sitting there, and (if two frames render the same name) its own
///   earlier output. Reserving the path first, against BOTH the filesystem and the names already
///   handed out in this batch, makes the collision a decision instead of a loss.
///
/// Adapted from NexFilm's export writer (GPL-3.0, github.com/BillyDu-TJ/NexFilm).
/// </summary>
public static class ExportFile
{
    /// <summary>
    /// Marks a file as an export in flight. Anything wearing this prefix is either being written
    /// right now or was orphaned by a crash, so <see cref="CleanupStale"/> may delete it — which
    /// is exactly why it must be a prefix nothing else would ever produce. The leading dot also
    /// hides the staging file on Unix while it exists.
    /// </summary>
    public const string TempPrefix = ".openrevelare-export-";

    private static int _sequence;

    /// <summary>What to do when the reserved path is already taken.</summary>
    public enum ConflictPolicy
    {
        /// <summary>Append " (2)", " (3)" … so nothing existing is touched.</summary>
        Unique,
        /// <summary>Replace the existing file. Collisions WITHIN the batch still get renamed —
        /// overwriting a file the user already had is a choice; overwriting the frame exported
        /// ten seconds ago is just data loss.</summary>
        Overwrite,
        /// <summary>Leave the existing file alone and export nothing for this frame.</summary>
        Skip,
    }

    /// <summary>
    /// Run <paramref name="writeTo"/> against a temporary sibling of <paramref name="path"/>, then
    /// move it onto the target. <paramref name="writeTo"/> receives the path it should write and
    /// may be any encoder that takes a filename.
    /// </summary>
    public static void Write(string path, Action<string> writeTo)
    {
        string full = Path.GetFullPath(path);
        string directory = Path.GetDirectoryName(full)
            ?? throw new IOException($"export path has no directory: {path}");
        string name = Path.GetFileName(full);
        Directory.CreateDirectory(directory);

        // Same directory as the target, deliberately: a rename is only atomic within one volume,
        // and the system temp folder is regularly on another one.
        for (int attempt = 0; attempt < 128; attempt++)
        {
            string temp = Path.Combine(directory,
                $"{TempPrefix}{Environment.ProcessId}-{Interlocked.Increment(ref _sequence)}-{name}");
            try
            {
                // CreateNew claims the name; it never adopts one a concurrent export owns.
                using (new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { }
            }
            catch (IOException) when (File.Exists(temp)) { continue; }

            try
            {
                writeTo(temp);
                // The rename is atomic but says nothing about when the DATA lands. Forcing the
                // bytes out first means a power cut can cost the new export and never the old
                // file: the name cannot flip to content that isn't on the platter yet.
                using (var handle = new FileStream(temp, FileMode.Open, FileAccess.Write, FileShare.None))
                    handle.Flush(flushToDisk: true);
                File.Move(temp, full, overwrite: true);
                return;
            }
            catch
            {
                try { File.Delete(temp); } catch { /* the stale sweep will get it */ }
                throw;
            }
        }

        throw new IOException($"could not allocate a temporary export name in {directory}");
    }

    /// <summary>
    /// Delete staging files orphaned in <paramref name="directory"/> by a previous crash. Call it
    /// once when an export starts, never while one is running: a temp another export currently
    /// holds open cannot be deleted and is skipped, but there is no reason to try.
    /// </summary>
    /// <returns>How many were removed. Failures are swallowed — a sweep that cannot run must
    /// not be the reason an export does not.</returns>
    public static int CleanupStale(string directory)
    {
        int removed = 0;
        try
        {
            foreach (string file in Directory.EnumerateFiles(directory, TempPrefix + "*"))
            {
                try { File.Delete(file); removed++; }
                catch { /* held by a live export, or already gone */ }
            }
        }
        catch { /* unreadable directory is the export's problem to report, not the sweep's */ }
        return removed;
    }

    /// <summary>The set <see cref="Reserve"/> expects — paths compared the way a filesystem that
    /// ignores case would, because on Windows one is.</summary>
    public static HashSet<string> NewReservations() => new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Claim an output path for <paramref name="stem"/>.<paramref name="extension"/> under
    /// <paramref name="policy"/>, recording it in <paramref name="reserved"/> so later frames in
    /// the same batch cannot claim it too. Reserving is separate from writing on purpose: every
    /// name in a batch can be settled before the first expensive render starts.
    /// </summary>
    /// <returns>The path to write, or null when the policy is <see cref="ConflictPolicy.Skip"/>
    /// and something is already there.</returns>
    public static string? Reserve(string directory, string stem, string extension,
                                  ConflictPolicy policy, ISet<string> reserved)
    {
        string Candidate(string value) => Path.GetFullPath(Path.Combine(directory, value + "." + extension));

        string first = Candidate(stem);
        if (!reserved.Contains(first))
        {
            switch (policy)
            {
                case ConflictPolicy.Overwrite:
                    reserved.Add(first);
                    return first;
                case ConflictPolicy.Skip when File.Exists(first):
                    return null;
                case ConflictPolicy.Skip:
                case ConflictPolicy.Unique when !File.Exists(first):
                    reserved.Add(first);
                    return first;
            }
        }
        else if (policy == ConflictPolicy.Skip) return null;

        for (int suffix = 2; suffix <= 100_000; suffix++)
        {
            string candidate = Candidate($"{stem} ({suffix})");
            if (!File.Exists(candidate) && reserved.Add(candidate)) return candidate;
        }

        throw new IOException($"no free export name for {stem}.{extension} in {directory}");
    }
}
