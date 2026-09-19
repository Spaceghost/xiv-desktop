namespace XivDesktop.Core.Input;

/// <summary>The actions a key chord can run, and the sway-style default bindings.</summary>
public static class KeyAction
{
    public const string Launcher = "launcher";
    public const string Terminal = "terminal";
    public const string Close = "close";
    public const string TogglePet = "toggle-pet";
    public const string PinFront = "pin-front";
    public const string FocusPrev = "focus-prev";
    public const string FocusNext = "focus-next";

    /// <summary>"workspace-3": switch to workspace 3.</summary>
    public static string Workspace(int n) => "workspace-" + n;

    /// <summary>"move-3": move the focused panel to workspace 3.</summary>
    public static string MoveTo(int n) => "move-" + n;

    /// <summary>Workspace number of a workspace-N or move-N action, else 0.</summary>
    public static int WorkspaceNumber(string action, out bool move)
    {
        move = action.StartsWith("move-", StringComparison.Ordinal);
        var prefix = move ? "move-" : "workspace-";
        return action.StartsWith(prefix, StringComparison.Ordinal) && int.TryParse(action.AsSpan(prefix.Length), out var n) && n is >= 1 and <= Workspaces.WorkspaceModel.Count ? n : 0;
    }

    public static string Describe(string action)
    {
        var n = WorkspaceNumber(action, out var move);
        if (n > 0)
            return move ? $"Move focused panel to workspace {n}" : $"Switch to workspace {n}";
        return action switch
        {
            Launcher => "Open the launcher",
            Terminal => "Open a terminal",
            Close => "Close the focused window panel",
            TogglePet => "Toggle focused panel: pet / pinned",
            PinFront => "Pin focused panel in front of you",
            FocusPrev => "Focus the previous window panel",
            FocusNext => "Focus the next window panel",
            _ => action,
        };
    }

    /// <summary>Every action, in settings order.</summary>
    public static IReadOnlyList<string> All { get; } = BuildAll();

    private static List<string> BuildAll()
    {
        var all = new List<string> { Launcher, Terminal, Close, TogglePet, PinFront, FocusPrev, FocusNext };
        for (var i = 1; i <= Workspaces.WorkspaceModel.Count; i++)
            all.Add(Workspace(i));
        for (var i = 1; i <= Workspaces.WorkspaceModel.Count; i++)
            all.Add(MoveTo(i));
        return all;
    }
}

/// <summary>The modifier every default binding is built on.</summary>
public enum ModifierPreset
{
    /// <summary>Super (the Windows/logo key), as in sway. A desktop may grab it before Wine sees it.</summary>
    Super,

    /// <summary>Alt+Shift: free under GNOME and sway defaults; Shift variants become Alt+Shift+Ctrl.</summary>
    AltShift,

    /// <summary>Ctrl+Alt; Shift variants become Ctrl+Alt+Shift.</summary>
    CtrlAlt,
}

public static class Keybinds
{
    public static KeyMods Base(ModifierPreset preset) => preset switch
    {
        ModifierPreset.AltShift => KeyMods.Alt | KeyMods.Shift,
        ModifierPreset.CtrlAlt => KeyMods.Ctrl | KeyMods.Alt,
        _ => KeyMods.Super,
    };

    /// <summary>
    /// The sway-like defaults on a base modifier. Bindings that add Shift in sway (move to workspace, pin in
    /// front) add Ctrl instead when the base already holds Shift, so no two actions share a chord.
    /// </summary>
    public static Dictionary<string, string> Defaults(ModifierPreset preset = ModifierPreset.Super)
    {
        var mod = Base(preset);
        var extra = (mod & KeyMods.Shift) != 0 ? KeyMods.Ctrl : KeyMods.Shift;
        string C(KeyMods m, int vk) => new KeyChord(m, vk).ToString();

        var d = new Dictionary<string, string>
        {
            [KeyAction.Launcher] = C(mod, 'D'),
            [KeyAction.Terminal] = C(mod, 0x0D),
            [KeyAction.Close] = C(mod, 'Q'),
            [KeyAction.TogglePet] = C(mod, 0x20),
            [KeyAction.PinFront] = C(mod | extra, 0x20),
            [KeyAction.FocusPrev] = C(mod, 0x25),
            [KeyAction.FocusNext] = C(mod, 0x27),
        };
        for (var i = 1; i <= Workspaces.WorkspaceModel.Count; i++)
        {
            d[KeyAction.Workspace(i)] = C(mod, '0' + i);
            d[KeyAction.MoveTo(i)] = C(mod | extra, '0' + i);
        }

        return d;
    }

    /// <summary>Parses a configured map; unparsable or empty entries are skipped (the action is unbound).</summary>
    public static List<(string Action, KeyChord Chord)> Compile(IReadOnlyDictionary<string, string> map, List<string>? errors = null)
    {
        var list = new List<(string, KeyChord)>();
        foreach (var action in KeyAction.All)
        {
            if (!map.TryGetValue(action, out var text) || string.IsNullOrWhiteSpace(text))
                continue;
            if (KeyChord.TryParse(text, out var chord))
                list.Add((action, chord));
            else
                errors?.Add($"{action}: \"{text}\" is not a key chord");
        }

        return list;
    }

    /// <summary>Pairs of actions bound to the same chord.</summary>
    public static List<(string A, string B, KeyChord Chord)> Conflicts(IReadOnlyList<(string Action, KeyChord Chord)> bindings)
    {
        var conflicts = new List<(string, string, KeyChord)>();
        for (var i = 0; i < bindings.Count; i++)
        {
            for (var j = i + 1; j < bindings.Count; j++)
            {
                if (bindings[i].Chord == bindings[j].Chord)
                    conflicts.Add((bindings[i].Action, bindings[j].Action, bindings[i].Chord));
            }
        }

        return conflicts;
    }
}

/// <summary>
/// Edge-triggered chord matching over a key-down query. Call <see cref="Update"/> once per frame: an action
/// fires once when its chord becomes held, not again until it is released. While <c>blocked</c> (an ImGui
/// text field wants the keyboard) nothing fires, and chords already held then do not fire on unblocking.
/// </summary>
public sealed class ChordTracker
{
    private readonly HashSet<KeyChord> held = [];

    public IReadOnlyList<(string Action, KeyChord Chord)> Bindings { get; set; } = [];

    /// <summary>Actions that fired this frame, and the keys of every bound chord currently held (to swallow from the game).</summary>
    public (List<string> Fired, List<int> Consumed) Update(Func<int, bool> isDown, bool blocked)
    {
        var fired = new List<string>();
        var consumed = new List<int>();
        var mods = KeyChord.ModsFrom(isDown);
        var now = new HashSet<KeyChord>();
        foreach (var (action, chord) in Bindings)
        {
            if (chord.IsNone || chord.Mods != mods || !isDown(chord.Key))
                continue;
            now.Add(chord);
            if (blocked)
                continue;
            consumed.Add(chord.Key);
            if (!held.Contains(chord))
                fired.Add(action);
        }

        held.Clear();
        held.UnionWith(now);
        return (fired, consumed);
    }

    public void Reset() => held.Clear();
}
