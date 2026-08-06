using System;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace OpenRevelare.Gui.Services;

/// <summary>
/// Best-effort update check against the project's GitHub Releases. Returns the latest release
/// only when it advertises a newer version than the running build. Any network/parse/rate-limit
/// error returns null silently — a failed update check must never surface as an error.
/// </summary>
public static class Updater
{
    public const string Repo = "Toshihiko-Lin/Open-Revelare";
    public const string LatestReleaseUrl = $"https://api.github.com/repos/{Repo}/releases/latest";
    public const string ReleasesPageUrl = $"https://github.com/{Repo}/releases/latest";

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
    /// Fetches the latest release and returns it only when it is newer than <paramref
    /// name="currentVersion"/>. 6 s for the background check at startup; the manual 检查更新…
    /// menu item passes 8 s.
    /// </summary>
    public static async Task<UpdateInfo?> CheckAsync(string currentVersion, int timeoutSec = 6)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(timeoutSec) };
            // GitHub rejects requests without a User-Agent outright.
            http.DefaultRequestHeaders.UserAgent.ParseAdd($"OpenRevelare/{currentVersion}");
            http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            string json = await http.GetStringAsync(LatestReleaseUrl);
            if (JsonNode.Parse(json) is not JsonObject d) return null;

            // Tags are "v1.2.3"; the comparison wants bare dotted numbers.
            string remote = (d["tag_name"]?.GetValue<string>() ?? "").TrimStart('v', 'V');
            if (remote.Length == 0 || Compare(remote, currentVersion) <= 0) return null;

            string date = d["published_at"]?.GetValue<string>() ?? "";
            if (date.Length >= 10) date = date[..10];        // 2026-08-06T…Z → 2026-08-06

            return new UpdateInfo(remote, date, PlatformAssetUrl(d),
                                  d["body"]?.GetValue<string>() ?? "");
        }
        catch { return null; }
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
