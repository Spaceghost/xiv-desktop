using Dalamud.Configuration;
using Dalamud.Plugin;
using XivDesktop.Core.Input;
using XivDesktop.Core.Workspaces;

namespace XivDesktop.Plugin;

/// <summary>
/// Persisted by Dalamud to pluginConfigs/XivDesktop.json. Keep collection defaults empty: Newtonsoft appends
/// into pre-populated collections on load. Written only on the framework thread; lists are replaced
/// wholesale (never mutated in place) so IPC readers on other threads see a consistent reference.
/// </summary>
[Serializable]
public sealed class Configuration : IPluginConfiguration
{
    public const int CurrentVersion = 2;

    public int Version { get; set; } = CurrentVersion;

    /// <summary>Desktop-file ids, in the order they were added.</summary>
    public List<string> Favourites { get; set; } = [];

    /// <summary>Desktop-file ids, most recent first (at most UserLists.MaxRecents).</summary>
    public List<string> Recents { get; set; } = [];

    /// <summary>Linux home directory override (e.g. "/home/name"). Empty: detect it from $HOME / WINEHOMEDIR / the config path.</summary>
    public string HomeOverride { get; set; } = "";

    /// <summary>Launcher grid icon size in pixels (before global scale).</summary>
    public int IconSize { get; set; } = 48;

    /// <summary>Close the launcher window after a successful launch.</summary>
    public bool CloseOnLaunch { get; set; } = true;

    // v1 ---------------------------------------------------------------------------------------

    /// <summary>The key chords are read at all.</summary>
    public bool KeybindsEnabled { get; set; } = true;

    /// <summary>The modifier the defaults were built on (Settings → Keybinds → preset).</summary>
    public ModifierPreset ModifierPreset { get; set; } = ModifierPreset.Super;

    /// <summary>Action → chord text ("Super+D"). Empty on first load; filled from the preset.</summary>
    public Dictionary<string, string> Keybinds { get; set; } = [];

    /// <summary>
    /// Read every key with GetAsyncKeyState instead of the game's key state, so chords also work while a
    /// window panel has the keyboard. Off by default: the game's key state is the normal Dalamud way.
    /// </summary>
    public bool ReadKeysGlobally { get; set; }

    /// <summary>What Super+Enter posts to ghostty-dalamud (a /term line without "/term") when terminal.new is not available.</summary>
    public string TerminalLine { get; set; } = "new";

    /// <summary>terminal.new profile for Super+Enter and terminal apps: a name or a 1-based number; "" = ghostty's default.</summary>
    public string TerminalProfile { get; set; } = "";

    /// <summary>terminal.new pin for Super+Enter and terminal apps ("pet", "here", …).</summary>
    public string TerminalPin { get; set; } = "pet";

    /// <summary>/term pin arguments for turning a pet into a pin (Super+Space on a pet).</summary>
    public string PinArgs { get; set; } = "here";

    /// <summary>/term pin arguments for Super+Shift+Space.</summary>
    public string PinFrontArgs { get; set; } = "here";

    public bool NotifyOpened { get; set; } = true;

    public bool NotifyEnded { get; set; } = true;

    /// <summary>Hide other workspaces' panels (window.place "hide"). Off: workspaces only group.</summary>
    public bool WorkspacesHide { get; set; } = true;

    public int CurrentWorkspace { get; set; } = 1;

    public List<WorkspaceEntry> Workspaces { get; set; } = [];

    /// <summary>The palette closes when it loses keyboard focus (Walker/Raycast style).</summary>
    public bool PaletteCloseOnFocusLoss { get; set; } = true;

    // Claude in game -----------------------------------------------------------------------------

    /// <summary>The /claude panel, the agent connection and the NPC are available at all.</summary>
    public bool ClaudeEnabled { get; set; } = true;

    /// <summary>ghostty-agent's address; empty means 127.0.0.1.</summary>
    public string ClaudeAgentHost { get; set; } = "";

    /// <summary>ghostty-agent's port; 0 means 7777.</summary>
    public int ClaudeAgentPort { get; set; }

    /// <summary>The agent's token file as a Linux path; empty means ~/.config/ghostty-agent/token.</summary>
    public string ClaudeTokenPath { get; set; } = "";

    /// <summary>Where a new session runs, as a Linux path. Empty means the home directory.</summary>
    public string ClaudeWorkingDirectory { get; set; } = "";

    /// <summary>Model alias for new sessions; empty leaves the account default.</summary>
    public string ClaudeModel { get; set; } = "";

    /// <summary>The claude executable on the agent's host; empty means "claude" on its PATH.</summary>
    public string ClaudeExecutable { get; set; } = "";

    /// <summary>
    /// The permission mode used only when the prompt-tool route is unavailable. Nothing is auto-approved
    /// beyond what this mode already allows, and the panel says so.
    /// </summary>
    public string ClaudePermissionMode { get; set; } = "manual";

    /// <summary>Tools the player chose "always allow" for. The only permission choice that is persisted.</summary>
    public List<string> ClaudeAlwaysAllow { get; set; } = [];

    /// <summary>Remembered sessions, newest first (see SessionHistory).</summary>
    public List<Core.Claude.SessionRecord> ClaudeSessions { get; set; } = [];

    /// <summary>Ask the model, once per new session, for its own name and look.</summary>
    public bool ClaudeAskForLook { get; set; } = true;

    /// <summary>Stream replies as they are written (--include-partial-messages).</summary>
    public bool ClaudeStreamPartials { get; set; } = true;

    /// <summary>Summon an NPC beside the player for each session, when a spawner is available.</summary>
    public bool ClaudeNpc { get; set; } = true;

    /// <summary>Extra claude arguments, space separated, appended verbatim.</summary>
    public string ClaudeExtraArgs { get; set; } = "";
    // Ask an NPC ---------------------------------------------------------------------------------

    /// <summary>
    /// Also answer "/ask" with the NPC conversation. Off by default: ghostty-dalamud owns /ask (its chat
    /// panel), and Dalamud gives a command to whoever registered it first, so this only takes effect when
    /// no other plugin holds /ask. "/npc" always works.
    /// </summary>
    public bool AskRouteSlashAsk { get; set; }

    /// <summary>Summon a local character to talk to (off: the dialogue alone).</summary>
    public bool AskSpawnNpc { get; set; } = true;

    /// <summary>The speaker: "npc:&lt;ENpcResident id&gt;", "minion:&lt;id&gt;", "mount:&lt;id&gt;", "pet:&lt;id&gt;" or "self".</summary>
    public string AskSpeaker { get; set; } = "";

    public List<string> AskFavourites { get; set; } = [];

    public List<string> AskRecents { get; set; } = [];

    /// <summary>Where questions go: the in-world dialogue over almanac's gateway, or ghostty's terminal "/term ask".</summary>
    public AskBackendKind AskBackend { get; set; } = AskBackendKind.Gateway;

    /// <summary>almanac gateway base URL (the plugin runs under Wine; loopback reaches the host).</summary>
    public string AskGateway { get; set; } = "";

    /// <summary>Model name sent to the gateway ("local" maps to its default model).</summary>
    public string AskModel { get; set; } = "local";

    /// <summary>Text speed, 1 (slow) … 5 (instant).</summary>
    public int AskReadingSpeed { get; set; } = 3;

    /// <summary>The summoned character follows the player like a pet.</summary>
    public bool AskFollow { get; set; } = true;

    /// <summary>Emote cues (the model's tags, else a keyword guess) play on the speaker and on your character.</summary>
    public bool AskEmotes { get; set; } = true;

    /// <summary>Your character turns toward the speaker and gestures when you ask.</summary>
    public bool AskPlayerGestures { get; set; } = true;

    /// <summary>Label for the "Myself" speaker's lines: "You (advising)".</summary>
    public string AskSelfTitle { get; set; } = "advising";

    /// <summary>Pet name → ModelChara row, learned from your own summoned pets (the sheets do not link them).</summary>
    public Dictionary<string, int> AskPetModels { get; set; } = [];

    public static Configuration Load(IDalamudPluginInterface pi)
    {
        Configuration config;
        try
        {
            config = pi.GetPluginConfig() as Configuration ?? new Configuration();
        }
        catch
        {
            config = new Configuration();
        }

        config.Favourites ??= [];
        config.Recents ??= [];
        config.HomeOverride ??= "";
        config.IconSize = Math.Clamp(config.IconSize, 24, 128);
        config.Keybinds ??= [];
        if (config.Keybinds.Count == 0)
            config.Keybinds = Core.Input.Keybinds.Defaults(config.ModifierPreset);
        config.TerminalLine = string.IsNullOrWhiteSpace(config.TerminalLine) ? "new" : config.TerminalLine;
        config.PinArgs = string.IsNullOrWhiteSpace(config.PinArgs) ? "here" : config.PinArgs;
        config.PinFrontArgs = string.IsNullOrWhiteSpace(config.PinFrontArgs) ? "here" : config.PinFrontArgs;
        config.Workspaces ??= [];
        config.TerminalProfile ??= "";
        config.TerminalPin = string.IsNullOrWhiteSpace(config.TerminalPin) ? "pet" : config.TerminalPin;
        config.CurrentWorkspace = Math.Clamp(config.CurrentWorkspace, 1, WorkspaceModel.Count);
        config.ClaudeAlwaysAllow ??= [];
        config.ClaudeSessions ??= [];
        config.ClaudeAgentHost ??= "";
        config.ClaudeTokenPath ??= "";
        config.ClaudeWorkingDirectory ??= "";
        config.ClaudeModel ??= "";
        config.ClaudeExecutable ??= "";
        config.ClaudeExtraArgs ??= "";
        config.ClaudePermissionMode = string.IsNullOrWhiteSpace(config.ClaudePermissionMode) ? "manual" : config.ClaudePermissionMode;
        config.AskSpeaker ??= "";
        config.AskFavourites ??= [];
        config.AskRecents ??= [];
        config.AskGateway ??= "";
        config.AskModel = string.IsNullOrWhiteSpace(config.AskModel) ? "local" : config.AskModel;
        config.AskReadingSpeed = Math.Clamp(config.AskReadingSpeed, 1, 5);
        config.AskSelfTitle ??= "advising";
        config.AskPetModels ??= [];
        config.Version = CurrentVersion;
        return config;
    }
}

public enum AskBackendKind
{
    /// <summary>almanac's model gateway (streamed, multi-turn), in the in-game dialogue.</summary>
    Gateway,

    /// <summary>ghostty-dalamud's terminal: posts "/term ask …" (one shot, terminal look).</summary>
    Terminal,
}
