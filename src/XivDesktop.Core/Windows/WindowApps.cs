namespace XivDesktop.Core.Windows;

/// <summary>Which catalog app a window panel most likely belongs to, for its icon.</summary>
public static class WindowApps
{
    /// <summary>
    /// Best guess, in order: an app id remembered from our own launch, the program of the app's command
    /// equal to the panel's <c>app</c> (what ghostty reports for a run command), a desktop-file id containing
    /// it, then an app whose name the window title ends with ("notes - Text Editor"). Null when nothing fits.
    /// </summary>
    public static AppInfo? Find(IReadOnlyList<AppInfo> apps, WindowPanel w, string? launchedId = null)
    {
        if (launchedId != null)
        {
            foreach (var a in apps)
            {
                if (string.Equals(a.Id, launchedId, StringComparison.OrdinalIgnoreCase))
                    return a;
            }
        }

        var prog = w.App.Length > 0 ? Program(w.App) : "";
        if (prog.Length > 0 && !Wrappers.Contains(prog))
        {
            foreach (var a in apps)
            {
                if (string.Equals(Program(a.Command), prog, StringComparison.OrdinalIgnoreCase))
                    return a;
            }

            foreach (var a in apps)
            {
                var id = a.Id.EndsWith(".desktop", StringComparison.OrdinalIgnoreCase) ? a.Id[..^8] : a.Id;
                if (id.Equals(prog, StringComparison.OrdinalIgnoreCase) || id.EndsWith("." + prog, StringComparison.OrdinalIgnoreCase))
                    return a;
            }
        }

        if (w.Title.Length > 0)
        {
            foreach (var a in apps)
            {
                if (a.Name.Length > 2 && (w.Title.Equals(a.Name, StringComparison.OrdinalIgnoreCase) || w.Title.EndsWith(" - " + a.Name, StringComparison.OrdinalIgnoreCase)))
                    return a;
            }
        }

        return null;
    }

    // Launchers that front many apps: their name says nothing about which app a window is.
    private static readonly HashSet<string> Wrappers = new(StringComparer.OrdinalIgnoreCase) { "flatpak", "env", "sh", "bash", "gtk-launch", "gio", "xdg-open", "distrobox-enter" };

    /// <summary>The basename of a command's first word, without quotes ("/usr/bin/yad --calendar" → "yad").</summary>
    public static string Program(string command)
    {
        var c = command.Trim();
        if (c.Length == 0)
            return "";
        string first;
        if (c[0] is '\'' or '"')
        {
            var end = c.IndexOf(c[0], 1);
            first = end > 0 ? c[1..end] : c[1..];
        }
        else
        {
            var sp = c.IndexOf(' ');
            first = sp < 0 ? c : c[..sp];
        }

        var slash = first.LastIndexOf('/');
        return slash >= 0 ? first[(slash + 1)..] : first;
    }
}
