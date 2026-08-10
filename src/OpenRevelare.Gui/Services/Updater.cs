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
/// TWO MIRRORS, asked at the same time:
/// <list type="bullet">
///   <item><b>GitHub Releases</b> — the canonical one.</item>
///   <item><b>Gitee Releases</b> — the same builds, moved across by hand at release time.</item>
/// </list>
/// api.github.com is unreachable from much of mainland China, which is most of this project's
/// users: on GitHub alone their check does not fail, it hangs and then silently gives up, and
/// they never hear that a release happened.
///
/// BOTH links are handed back, not one. An earlier version raced the two and returned whichever
/// answered first, preferring GitHub whenever it was done — which meant a reachable GitHub always
/// won and the Gitee link was unreachable in practice, while users behind the block only saw Gitee
/// because GitHub had burnt the whole timeout first. Reachability is not something this code can
/// infer: a proxied GitHub answers the API fine and still cannot serve a release asset. So the
/// notice offers both and lets the user pick, and <see cref="CheckAsync"/> waits for both.
///
/// The two APIs are near enough identical — <c>tag_name</c>, a Markdown <c>body</c>, and an
/// <c>assets</c> array of name + browser_download_url — that one parser reads both. Gitee tags
/// are bare ("1.0.0") where GitHub's carry a v ("v1.0.0"), and Gitee dates the release with
/// <c>created_at</c> rather than <c>published_at</c>; both are handled in <see cref="FromApiAsync"/>.
///
/// NOT the version.json manifest. That one still exists and is still what the pre-1.0 Python
/// build (APP_VERSION 0.8.0) polls, but it carries a single download_url — one Windows installer
/// for everybody — so it cannot tell a Linux user where their AppImage is. Leave it to the old
/// app; releases from 1.0.0 on are found through the release APIs above.
/// </summary>
public static class Updater
{
    public const string Repo = "Toshihiko-Lin/Open-Revelare";
    public const string LatestReleaseUrl = $"https://api.github.com/repos/{Repo}/releases/latest";
    public const string ReleasesPageUrl = $"https://github.com/{Repo}/releases/latest";

    /// <summary>The Gitee mirror still carries the pre-open-source product name, and its assets
    /// are still called Revelare-*. Nothing here matches on the product name — assets are picked
    /// by extension and architecture — so the mirror can be renamed without touching this.</summary>
    public const string GiteeRepo = "Toshihiko-Lin/revelare-release";
    public const string GiteeLatestReleaseUrl = $"https://gitee.com/api/v5/repos/{GiteeRepo}/releases/latest";
    public const string GiteeReleasesPageUrl = $"https://gitee.com/{GiteeRepo}/releases/latest";

    /// <summary>
    /// <see cref="Changelog"/> is always PLAIN TEXT — release bodies are Markdown and the notice
    /// dialog has no renderer, so flattening happens here rather than at the call site.
    ///
    /// <see cref="MirrorDownloadUrl"/> is the Gitee link and is empty when that mirror did not
    /// answer, did not carry this version yet (it is copied across by hand, so it can lag), or
    /// when Gitee is itself the source — in which case it is <see cref="DownloadUrl"/> that holds
    /// the Gitee link. The notice shows a second button only when this is non-empty.
    /// </summary>
    public sealed record UpdateInfo(
        string Version, string ReleaseDate, string DownloadUrl, string Changelog,
        string MirrorDownloadUrl = "");

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
    /// Asks both mirrors and returns a release only when it is newer than <paramref
    /// name="currentVersion"/>. 6 s for the background check at startup; the manual 检查更新…
    /// menu item passes 8 s.
    ///
    /// Waits for BOTH so the notice can offer both links. That costs a blocked GitHub its full
    /// timeout, which is the price of not guessing which host the user can actually download
    /// from — and the wait is unattended anyway: the startup check runs 3 s after the window is
    /// already up, and the manual one shows 正在检查更新 …
    /// </summary>
    public static async Task<UpdateInfo?> CheckAsync(string currentVersion, int timeoutSec = 6)
    {
        UpdateInfo?[] found = await Task.WhenAll(
            FromApiAsync(LatestReleaseUrl, ReleasesPageUrl, currentVersion, timeoutSec),
            FromApiAsync(GiteeLatestReleaseUrl, GiteeReleasesPageUrl, currentVersion, timeoutSec));
        UpdateInfo? github = found[0], gitee = found[1];

        // Neither mirror advertised anything newer — or neither answered. Same silence either way.
        if (github is null && gitee is null) return null;

        // GitHub is canonical, so its release notes and version are the ones shown when both
        // answered; Gitee rides along as the second button. Where only Gitee answered it becomes
        // the whole notice, with no second button to offer.
        if (github is null) return gitee;
        if (gitee is null) return github;

        // Both answered. They are the same build unless the hand-copy to Gitee has not happened
        // yet, in which case Gitee still advertises the previous release — offering that as a
        // download for the version named in the notice would hand the user the wrong installer.
        return github with { MirrorDownloadUrl = gitee.Version == github.Version ? gitee.DownloadUrl : "" };
    }

    /// <summary>
    /// One mirror. <paramref name="pageUrl"/> is where the download button goes when the release
    /// carries nothing for this platform — a landing page still beats a dead button, and it is
    /// also what a source-only release should hand back.
    /// </summary>
    private static async Task<UpdateInfo?> FromApiAsync(
        string apiUrl, string pageUrl, string currentVersion, int timeoutSec)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(timeoutSec) };
            // GitHub rejects requests without a User-Agent outright; Gitee does not mind one.
            http.DefaultRequestHeaders.UserAgent.ParseAdd($"OpenRevelare/{currentVersion}");
            http.DefaultRequestHeaders.Accept.ParseAdd("application/json");
            string json = await http.GetStringAsync(apiUrl);
            if (JsonNode.Parse(json) is not JsonObject d) return null;

            // GitHub tags are "v1.2.3", Gitee's are "1.2.3"; the comparison wants bare numbers.
            string remote = (d["tag_name"]?.GetValue<string>() ?? "").TrimStart('v', 'V');
            if (remote.Length == 0 || Compare(remote, currentVersion) <= 0) return null;

            // published_at is GitHub's, created_at is Gitee's.
            string date = d["published_at"]?.GetValue<string>()
                          ?? d["created_at"]?.GetValue<string>() ?? "";
            if (date.Length >= 10) date = date[..10];        // 2026-08-06T…Z → 2026-08-06

            return new UpdateInfo(remote, date, PlatformAssetUrl(d, pageUrl),
                                  MarkdownToText(d["body"]?.GetValue<string>() ?? ""));
        }
        catch { return null; }
    }

    /// <summary>
    /// The release asset for the platform we are running on, matched on the file name the
    /// packaging scripts produce (setup.exe / .AppImage / -arm64|-x86_64.dmg). Both mirrors use
    /// the same names. Gitee additionally lists the source archives it generates for every tag
    /// (1.0.0.zip, 1.0.0.tar.gz); neither matches an extension below, so they are skipped.
    /// </summary>
    private static string PlatformAssetUrl(JsonObject d, string pageUrl)
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
        // Gitee's response has no html_url, hence the caller-supplied page.
        return d["html_url"]?.GetValue<string>() ?? pageUrl;
    }

    /// <summary>
    /// Minimal Markdown→text for a release body. The notice is a plain TextBlock with no
    /// renderer, so <c>### macOS 首次打开</c> and <c>**Beta**</c> would otherwise reach the user
    /// as punctuation. A de-emphasiser, not a parser: it handles the marks this project's release
    /// notes actually use and leaves everything else — links included — legible as written.
    /// </summary>
    private static string MarkdownToText(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        s = s.Replace("\r\n", "\n");                                              // Gitee sends CRLF
        s = Regex.Replace(s, @"^\s{0,3}#{1,6}\s*", "", RegexOptions.Multiline);   // ### heading
        s = Regex.Replace(s, @"^\s{0,3}>\s?", "", RegexOptions.Multiline);        // > quote
        s = Regex.Replace(s, @"(\*\*|__)(.+?)\1", "$2", RegexOptions.Singleline); // **bold**
        s = Regex.Replace(s, "`([^`]*)`", "$1");                                  // `code`
        // Trim, and never let more than one blank line through: both mirrors write their notes
        // for a page with margins, and the notice is a dialog that has to stay one screen.
        return Regex.Replace(s.Trim(), @"\n{3,}", "\n\n");
    }

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
