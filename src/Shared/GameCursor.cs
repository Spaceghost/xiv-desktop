// Keeps the game's own cursor while the pointer is over XivDesktop's UI: ImGuiConfigFlags.NoMouseCursorChange
// tells Dalamud's backend not to replace it (read at the next NewFrame). The flag is set only while hovered
// and cleared only if this assembly set it, so other plugins' cursors are unaffected. Never SetMouseCursor.
using Dalamud.Bindings.ImGui;

namespace XivDesktop.Shared;

public static class GameCursor
{
    private static readonly HashSet<string> Hovering = [];
    private static bool owned;

    /// <summary>
    /// Call once per frame per UI part (<paramref name="source"/>), at the end of its drawing, with whether the
    /// pointer is over it. The flag stays set while any part of this assembly is hovered.
    /// </summary>
    public static void Update(string source, bool over)
    {
        if (over)
            Hovering.Add(source);
        else
            Hovering.Remove(source);
        Apply(Hovering.Count > 0);
    }

    private static void Apply(bool over)
    {
        var io = ImGui.GetIO();
        if (over)
        {
            if ((io.ConfigFlags & ImGuiConfigFlags.NoMouseCursorChange) == 0)
            {
                io.ConfigFlags |= ImGuiConfigFlags.NoMouseCursorChange;
                owned = true;
            }
        }
        else if (owned)
        {
            io.ConfigFlags &= ~ImGuiConfigFlags.NoMouseCursorChange;
            owned = false;
        }
    }

    /// <summary>Clear the flag if this assembly holds it (on unload).</summary>
    public static void Release()
    {
        try
        {
            Hovering.Clear();
            Apply(false);
        }
        catch
        {
            // No ImGui context during shutdown.
        }
    }
}
