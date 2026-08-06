using System.Reflection;

namespace OpenRevelare.Gui.Services;

/// <summary>
/// Application identity — the C# counterpart of Python's <c>negative/settings.py::APP_VERSION</c>.
///
/// The number is declared once, in <c>OpenRevelare.Gui.csproj</c> (<c>&lt;Version&gt;</c>), and read back
/// from the assembly here so the About box, the update check and the installer can never disagree.
/// It must stay ahead of the Python build's APP_VERSION (0.8.0) and match the <c>version</c> field
/// of the published manifest — see <see cref="Updater"/> and <c>RELEASE.md</c>.
/// </summary>
public static class AppInfo
{
    public static string Version =>
        Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";
}
