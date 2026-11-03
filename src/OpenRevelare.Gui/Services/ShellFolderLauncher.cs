using System.Diagnostics;

namespace OpenRevelare.Gui.Services;

/// <summary>Builds the platform command used to reveal a directory in the file manager.</summary>
internal static class ShellFolderLauncher
{
    public static ProcessStartInfo Create(string directory)
    {
        string fullPath = Path.GetFullPath(directory);
        if (OperatingSystem.IsWindows())
        {
            // Let the Windows shell open the directory itself. Calling explorer.exe with a quoted
            // argument passed through an extra parser and could return focus to the application's
            // adjacent “new roll” command instead of reliably revealing the folder.
            return new ProcessStartInfo
            {
                FileName = fullPath,
                UseShellExecute = true,
                Verb = "open",
            };
        }

        var info = new ProcessStartInfo(OperatingSystem.IsMacOS() ? "open" : "xdg-open")
        {
            UseShellExecute = false,
        };
        info.ArgumentList.Add(fullPath);
        return info;
    }
}
