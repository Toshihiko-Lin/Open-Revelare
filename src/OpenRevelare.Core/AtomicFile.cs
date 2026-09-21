namespace OpenRevelare.Core;

/// <summary>
/// Temp file + atomic replace, with a short retry on the sharing violation Windows hands out
/// when something else has the freshly written temp file or the target open — Defender and
/// the search indexer both do this for a moment after a write.
///
/// Why it matters: the project file is rewritten on every idle pause, so during a long roll
/// warm-up it is written every few seconds, and a scan that overlaps one write is a matter of
/// time. The idle-pause autosave shrugs and retries on the next tick, but the flush that runs
/// BEFORE a roll switch gets one attempt, and its failure used to be silent — the status line
/// was overwritten by 「正在打开工程 …」 and the in-memory edits were discarded by the load that
/// followed. A few short retries turn that transient into a non-event.
/// </summary>
public static class AtomicFile
{
    private const int Attempts = 6;
    private const int RetryDelayMs = 60;

    public static void WriteAllText(string path, string text)
    {
        string tmp = path + ".tmp";
        File.WriteAllText(tmp, text);
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                if (File.Exists(path)) File.Replace(tmp, path, null);
                else File.Move(tmp, path);
                return;
            }
            catch (IOException) when (attempt < Attempts)
            {
                Thread.Sleep(RetryDelayMs);
            }
            catch (UnauthorizedAccessException) when (attempt < Attempts)
            {
                Thread.Sleep(RetryDelayMs);
            }
        }
    }
}
