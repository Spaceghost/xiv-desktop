namespace XivDesktop.Core.Windows;

public enum WindowEventKind
{
    /// <summary>A panel became live (its window opened).</summary>
    Opened,

    /// <summary>A panel ended, or vanished from the list without being seen ending.</summary>
    Ended,
}

public sealed record WindowEvent(WindowEventKind Kind, WindowPanel Window);

/// <summary>What changed between two <c>window.list</c> snapshots, for notifications.</summary>
public static class WindowDiff
{
    public static List<WindowEvent> Diff(WindowListSnapshot before, WindowListSnapshot after)
    {
        var events = new List<WindowEvent>();
        foreach (var w in after.Windows)
        {
            var old = before.Find(w.Id);
            if (w.IsLive && (old == null || !old.IsLive))
                events.Add(new WindowEvent(WindowEventKind.Opened, w));
            else if (w.IsEnded && old is { IsEnded: false })
                events.Add(new WindowEvent(WindowEventKind.Ended, old.Title.Length > 0 && w.Title.Length == 0 ? w with { Title = old.Title } : w));
        }

        foreach (var old in before.Windows)
        {
            if (!old.IsEnded && after.Find(old.Id) == null)
                events.Add(new WindowEvent(WindowEventKind.Ended, old));
        }

        return events;
    }
}
