using System.Reflection;

namespace OpenRevelare.Gui.Services;

/// <summary>
/// Application identity — the C# counterpart of Python's <c>negative/settings.py::APP_VERSION</c>.
///
/// The number is declared once, in <c>OpenRevelare.Gui.csproj</c> (<c>&lt;Version&gt;</c>), and read back
/// from the assembly here so the About box, the update check and the installer can never disagree.
/// It must stay ahead of the Python build's APP_VERSION (0.8.0), and at release time it has to
/// match BOTH things a user's copy might compare itself against: the git tag (release.yml's guard
/// job fails the build when they differ) and the <c>version</c> field of the Gitee-hosted
/// manifest, which has to be pushed by hand afterwards. See <see cref="Updater"/> for why there
/// are two of them.
/// </summary>
public static class AppInfo
{
    public static string Version =>
        Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";
}
