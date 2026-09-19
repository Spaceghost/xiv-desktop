using System.Runtime.InteropServices;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Keys;
using Dalamud.Plugin.Services;
using XivDesktop.Core.Input;

namespace XivDesktop.Plugin.Services;

/// <summary>
/// Sway-style key chords in game. Once per framework tick: read the keys, match the configured chords
/// (edge-triggered, exact modifiers; <see cref="ChordTracker"/>), and swallow a consumed key from the game
/// by clearing it in <see cref="IKeyState"/>, the usual Dalamud way. Nothing fires while an ImGui text field
/// wants the keyboard.
/// <para>
/// Keys are read from the game's key state when it tracks them. The game does not necessarily track the
/// Windows keys (VK_LWIN/VK_RWIN), so those, and every key with <see cref="Configuration.ReadKeysGlobally"/>,
/// fall back to GetAsyncKeyState, and only while the game window is in the foreground.
/// </para>
/// </summary>
public sealed class KeybindService : IDisposable
{
    private readonly IFramework framework;
    private readonly IKeyState keys;
    private readonly IPluginLog log;
    private readonly Configuration config;
    private readonly Action<string> run;
    private readonly ChordTracker tracker = new();
    private readonly Dictionary<int, bool> validCache = [];
    private Dictionary<string, string>? compiledFrom;

    public KeybindService(IFramework framework, IKeyState keys, IPluginLog log, Configuration config, Action<string> run)
    {
        this.framework = framework;
        this.keys = keys;
        this.log = log;
        this.config = config;
        this.run = run;
        Recompile();
        framework.Update += OnUpdate;
    }

    public List<string> Errors { get; } = [];

    public List<(string A, string B, KeyChord Chord)> Conflicts { get; private set; } = [];

    /// <summary>The last action a chord ran and when, for the settings window.</summary>
    public (string Action, DateTime At)? LastFired { get; private set; }

    /// <summary>Re-reads <see cref="Configuration.Keybinds"/> (call after editing it).</summary>
    public void Recompile()
    {
        Errors.Clear();
        var bindings = Keybinds.Compile(config.Keybinds, Errors);
        Conflicts = Keybinds.Conflicts(bindings);
        tracker.Bindings = bindings;
        tracker.Reset();
        compiledFrom = config.Keybinds;
    }

    private void OnUpdate(IFramework fw)
    {
        if (!config.KeybindsEnabled)
            return;
        try
        {
            if (!ReferenceEquals(compiledFrom, config.Keybinds))
                Recompile();
            if (!GameInForeground())
            {
                tracker.Reset();
                return;
            }

            var blocked = ImGui.GetIO().WantTextInput;
            var (fired, consumed) = tracker.Update(IsDown, blocked);
            foreach (var vk in consumed)
            {
                if (IsValid(vk))
                    keys[(VirtualKey)vk] = false;
            }

            foreach (var action in fired)
            {
                LastFired = (action, DateTime.Now);
                run(action);
            }
        }
        catch (Exception ex)
        {
            log.Warning(ex, "XivDesktop: keybind update failed");
        }
    }

    private bool IsDown(int vk)
    {
        if (!config.ReadKeysGlobally && IsValid(vk))
            return keys[(VirtualKey)vk];
        return AsyncDown(vk);
    }

    private bool IsValid(int vk)
    {
        if (!validCache.TryGetValue(vk, out var valid))
        {
            try
            {
                valid = keys.IsVirtualKeyValid((VirtualKey)vk);
            }
            catch
            {
                valid = false;
            }

            validCache[vk] = valid;
        }

        return valid;
    }

    private static bool AsyncDown(int vk)
    {
        if (!OperatingSystem.IsWindows())
            return false;
        try
        {
            return (GetAsyncKeyState(vk) & 0x8000) != 0;
        }
        catch
        {
            return false;
        }
    }

    private static bool GameInForeground()
    {
        if (!OperatingSystem.IsWindows())
            return true;
        try
        {
            var hwnd = GetForegroundWindow();
            if (hwnd == IntPtr.Zero)
                return false;
            GetWindowThreadProcessId(hwnd, out var pid);
            return pid == (uint)Environment.ProcessId;
        }
        catch
        {
            return true;
        }
    }

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    public void Dispose() => framework.Update -= OnUpdate;
}
