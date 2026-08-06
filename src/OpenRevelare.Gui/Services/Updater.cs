using System;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace OpenRevelare.Gui.Services;

/// <summary>
/// Best-effort update check. Returns the newest advertised release only when it is ahead of the
/// running build. Any network/parse/rate-limit error returns null silently — a failed update
/// check must never surface as an error.
///
/// TWO CHANNELS, on purpose:
/// <list type="bullet">
///   <item><b>GitHub Releases</b> — the source of truth, and the only one that knows which asset
///   belongs to the platform we are running on.</item>
///   <item><b>The manifest</b> at <see cref="ManifestUrl"/> — a Netlify 302 onto a Gitee-hosted
///   version.json. This is the channel the pre-1.0 build already polls, so it is maintained and
///   reachable regardless of what GitHub is doing.</item>
/// </list>
/// api.github.com is unreachable from much of mainland China, which is most of this project's
/// users: on GitHub alone their check does not fail, it hangs and then silently gives up, and
/// they never hear that a release happened. The manifest is what makes the notice arrive at all
/// for them. It is also what keeps the check working while the repository is still private —
/// /releases/latest 404s for an anonymous client until the repo is public AND the draft release
/// has been published (that endpoint excludes drafts and prereleases).
///
/// Publishing therefore has TWO steps, and the second is manual: publish the GitHub release,
/// then push the new version.json to the Gitee release repo. Skipping the second one leaves
/// every mainland user on the old build with no way of knowing.
/// </summary>
public static class Updater
{
    public const string Repo = "Toshihiko-Lin/Open-Revelare";
    public const string LatestReleaseUrl = $"https://api.github.com/repos/{Repo}/releases/latest";
    public const string ReleasesPageUrl = $"https://github.com/{Repo}/releases/latest";

    /// <summary>The manifest the pre-1.0 build polls. 302s to raw.giteeusercontent.com; served
    /// with a 60 s cache, so a pushed version.json reaches users about a minute later.</summary>
    public const string ManifestUrl = "https://revelare.netlify.app/version.json";

    /// <summary>Landing page — where a download link points when nothing better is on offer.</summary>
    public const string SiteUrl = "https://revelare.netlify.app/";

    /// <summary><see cref="Changelog"/> is always PLAIN TEXT: the two channels write their notes
    /// in different markup (the manifest in HTML, GitHub in Markdown) and the notice dialog has
    /// no renderer for either, so flattening happens here rather than at the call site.</summary>
    public sealed record UpdateInfo(string Version, string ReleaseDate, string DownloadUrl, string Changelog);

    /// <summary>
    /// Opens a URL in the user's browser — the C# stand-in for Python's <c>webbrowser.open</c>,
    /// used by the update notice's 前往下载 button. Silent on failure: a missing browser must not
    /// take down the dialog that offered the link.
    /// </summary>
    public static void OpenUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        try
        {
            System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch { }
    }

    /// <summary>
    /// Asks both channels and returns a release only when it is newer than <paramref
    /// name="currentVersion"/>. 6 s for the background check at startup; the manual 检查更新…
    /// menu item passes 8 s.
    /// </summary>
    public static async Task<UpdateInfo?> CheckAsync(string currentVersion, int timeoutSec = 6)
    {
        // CONCURRENTLY, not GitHub-then-fallback. Sequential would spend the whole timeout on a
        // blocked github.com BEFORE the manifest was even tried — doubling the wait for exactly
        // the users the fallback exists for. Side by side, the check costs one timeout, which is
        // what it cost when there was only one channel.
        Task<UpdateInfo?> github = FromGitHubAsync(currentVersion, timeoutSec);
        Task<UpdateInfo?> manifest = FromManifestAsync(currentVersion, timeoutSec);
        await Task.WhenAll(github, manifest);
        // GitHub wins a tie: only it can name the asset for THIS platform, where the manifest
        // has one URL for everyone. Neither task throws — both swallow their own failures.
        return await github ?? await manifest;
    }

    private static HttpClient NewClient(string currentVersion, int timeoutSec)
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(timeoutSec) };
        // GitHub rejects requests without a User-Agent outright.
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"OpenRevelare/{currentVersion}");
        return http;
    }

    private static async Task<UpdateInfo?> FromGitHubAsync(string currentVersion, int timeoutSec)
    {
        try
        {
            using HttpClient http = NewClient(currentVersion, timeoutSec);
            http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            string json = await http.GetStringAsync(LatestReleaseUrl);
            if (JsonNode.Parse(json) is not JsonObject d) return null;

            // Tags are "v1.2.3"; the comparison wants bare dotted numbers.
            string remote = (d["tag_name"]?.GetValue<string>() ?? "").TrimStart('v', 'V');
            if (remote.Length == 0 || Compare(remote, currentVersion) <= 0) return null;

            string date = d["published_at"]?.GetValue<string>() ?? "";
            if (date.Length >= 10) date = date[..10];        // 2026-08-06T…Z → 2026-08-06

            return new UpdateInfo(remote, date, PlatformAssetUrl(d),
                                  MarkdownToText(d["body"]?.GetValue<string>() ?? ""));
        }
        catch { return null; }
    }

    /// <summary>
    /// The manifest channel: <c>{version, release_date, download_url, changelog}</c>, the shape
    /// the pre-1.0 build has always read. Kept tolerant of a missing field — this file is edited
    /// by hand at release time, and a typo in it must cost the notice, not the app.
    /// </summary>
    private static async Task<UpdateInfo?> FromManifestAsync(string currentVersion, int timeoutSec)
    {
        try
        {
            using HttpClient http = NewClient(currentVersion, timeoutSec);
            string json = await http.GetStringAsync(ManifestUrl);
            if (JsonNode.Parse(json) is not JsonObject d) return null;

            string remote = (d["version"]?.GetValue<string>() ?? "").TrimStart('v', 'V');
            if (remote.Length == 0 || Compare(remote, currentVersion) <= 0) return null;

            return new UpdateInfo(remote,
                                  d["release_date"]?.GetValue<string>() ?? "",
                                  ManifestDownloadUrl(d["download_url"]?.GetValue<string>() ?? ""),
                                  HtmlToText(d["changelog"]?.GetValue<string>() ?? ""));
        }
        catch { return null; }
    }

    /// <summary>
    /// The manifest carries ONE download_url, and today it is the Windows installer — it is
    /// shared with the pre-1.0 build, which only ever shipped for Windows. Handing that .exe to a
    /// Linux or macOS user as "your download" is worse than no link at all, so anything that is
    /// plainly not this platform's artifact degrades to the site's download page.
    /// </summary>
    private static string ManifestDownloadUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return SiteUrl;
        string name = url.ToLowerInvariant();
        bool fits = OperatingSystem.IsMacOS() ? name.Contains(".dmg")
                  : OperatingSystem.IsLinux() ? name.Contains(".appimage")
                  : name.Contains(".exe");
        return fits ? url : SiteUrl;
    }

    /// <summary>
    /// The release asset for the platform we are running on, matched on the file name the
    /// packaging scripts produce (setup.exe / .AppImage / -arm64|-x86_64.dmg). Falls back to the
    /// release page itself when nothing matches — a landing page still beats a dead button, and
    /// it is also what a source-only release should hand back.
    /// </summary>
    private static string PlatformAssetUrl(JsonObject d)
    {
        if (d["assets"] is JsonArray assets)
        {
            // macOS ships two dmgs, so the arch has to pick between them.
            string arch = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture
                          == System.Runtime.InteropServices.Architecture.Arm64 ? "arm64" : "x86_64";
            foreach (JsonNode? a in assets)
            {
                string name = (a?["name"]?.GetValue<string>() ?? "").ToLowerInvariant();
                string? url = a?["browser_download_url"]?.GetValue<string>();
                if (string.IsNullOrEmpty(url)) continue;

                bool hit = OperatingSystem.IsMacOS() ? name.EndsWith(".dmg") && name.Contains(arch)
                         : OperatingSystem.IsLinux() ? name.EndsWith(".appimage")
                         : name.EndsWith(".exe");
                if (hit) return url;
            }
        }
        return d["html_url"]?.GetValue<string>() ?? ReleasesPageUrl;
    }

    /// <summary>Minimal HTML→text for the manifest changelog (it uses &lt;b&gt; and &lt;br&gt;).</summary>
    private static string HtmlToText(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        s = Regex.Replace(s, @"<br\s*/?>", "\n", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, "<[^>]+>", "");
        return Collapse(s);
    }

    /// <summary>
    /// Minimal Markdown→text for a GitHub release body. The notice is a plain TextBlock with no
    /// renderer, so <c>### macOS 首次打开</c> and <c>**Beta**</c> would otherwise reach the user
    /// as punctuation. A de-emphasiser, not a parser: it handles the marks this project's release
    /// notes actually use and leaves everything else — links included — legible as written.
    /// </summary>
    private static string MarkdownToText(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        s = Regex.Replace(s, @"^\s{0,3}#{1,6}\s*", "", RegexOptions.Multiline);   // ### heading
        s = Regex.Replace(s, @"(\*\*|__)(.+?)\1", "$2", RegexOptions.Singleline); // **bold**
        s = Regex.Replace(s, "`([^`]*)`", "$1");                                  // `code`
        return Collapse(s);
    }

    /// <summary>Trim, and never let more than one blank line through — both sources are written
    /// for a page with margins, and the notice is a dialog that has to stay one screen.</summary>
    private static string Collapse(string s) => Regex.Replace(s.Trim(), @"\n{3,}", "\n\n");

    /// <summary>Compare dotted versions ("1.2.3") component-wise. &gt;0 when a is newer.</summary>
    private static int Compare(string a, string b)
    {
        int[] pa = Parse(a), pb = Parse(b);
        int n = Math.Max(pa.Length, pb.Length);
        for (int i = 0; i < n; i++)
        {
            int va = i < pa.Length ? pa[i] : 0, vb = i < pb.Length ? pb[i] : 0;
            if (va != vb) return va.CompareTo(vb);
        }
        return 0;
    }

    private static int[] Parse(string v)
    {
        string[] parts = v.Trim().Split('.');
        var outp = new int[parts.Length];
        for (int i = 0; i < parts.Length; i++) int.TryParse(parts[i], out outp[i]);
        return outp;
    }
}
