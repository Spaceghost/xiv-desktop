using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Windowing;
using XivDesktop.Core.Input;
using XivDesktop.Core.Workspaces;
using XivDesktop.Plugin.Services;

namespace XivDesktop.Plugin.Windows;

/// <summary>/desktop settings: general, keybinds, windows and workspaces, and what the backends report.</summary>
public sealed class SettingsWindow : Window
{
    private readonly Configuration config;
    private readonly DesktopService desktop;
    private readonly SessionService session;
    private readonly KeybindService keybinds;
    private readonly Dictionary<string, string> editing = [];

    public SettingsWindow(Configuration config, DesktopService desktop, SessionService session, KeybindService keybinds)
        : base("XivDesktop settings###XivDesktopSettings")
    {
        this.config = config;
        this.desktop = desktop;
        this.session = session;
        this.keybinds = keybinds;
        Size = new Vector2(560, 620);
        SizeCondition = ImGuiCond.FirstUseEver;
    }

    /// <summary>Draws the "Ask" tab (set by the Ask-an-NPC module).</summary>
    public Action? AskTab { get; set; }

    public override void OnOpen() => editing.Clear();

    public override void Draw()
    {
        if (!ImGui.BeginTabBar("##xivdesktop-settings"))
            return;
        if (ImGui.BeginTabItem("General"))
        {
            General();
            ImGui.EndTabItem();
        }

        if (ImGui.BeginTabItem("Keybinds"))
        {
            Keys();
            ImGui.EndTabItem();
        }

        if (ImGui.BeginTabItem("Windows"))
        {
            WindowsTab();
            ImGui.EndTabItem();
        }

        if (claude != null && ImGui.BeginTabItem("Claude"))
        {
            ClaudeTab();
            ImGui.EndTabItem();
        }

        if (AskTab is not null && ImGui.BeginTabItem("Ask"))
        {
            AskTab();
            ImGui.EndTabItem();
        }

        ImGui.EndTabBar();
    }

    /// <summary>Set once the Claude sessions are wired up; without it the tab is not shown.</summary>
    public Claude.ClaudeService? Claude
    {
        get => claude;
        set => claude = value;
    }

    private Claude.ClaudeService? claude;

    /// <summary>The /claude settings: the agent, where sessions run, the model, permissions and the NPC.</summary>
    private void ClaudeTab()
    {
        if (claude == null)
            return;
        var changed = false;

        var enabled = config.ClaudeEnabled;
        if (ImGui.Checkbox("Enable /claude", ref enabled))
        {
            config.ClaudeEnabled = enabled;
            changed = true;
        }

        var status = claude.Status();
        ImGui.TextColored(status.StartsWith("error", StringComparison.Ordinal) ? ImGuiColors.DalamudOrange : ImGuiColors.HealerGreen,
            status[(status.IndexOf(':') + 1)..].Trim());

        ImGui.Separator();
        ImGui.TextColored(ImGuiColors.DalamudGrey, "ghostty-agent");
        var host = config.ClaudeAgentHost;
        if (ImGui.InputTextWithHint("Host", "127.0.0.1", ref host, 128))
        {
            config.ClaudeAgentHost = host;
            changed = true;
        }

        var port = config.ClaudeAgentPort;
        if (ImGui.InputInt("Port (0 = 7777)", ref port))
        {
            config.ClaudeAgentPort = Math.Clamp(port, 0, 65535);
            changed = true;
        }

        var tokenPath = config.ClaudeTokenPath;
        if (ImGui.InputTextWithHint("Token file", "~/.config/ghostty-agent/token", ref tokenPath, 512))
        {
            config.ClaudeTokenPath = tokenPath;
            changed = true;
        }

        ImGui.TextColored(ImGuiColors.DalamudGrey, "The token is read only to authenticate and is never logged or stored here.");

        ImGui.Separator();
        ImGui.TextColored(ImGuiColors.DalamudGrey, "Sessions");
        var cwd = config.ClaudeWorkingDirectory;
        if (ImGui.InputTextWithHint("Working directory", claude.WorkingDirectory(), ref cwd, 512))
        {
            config.ClaudeWorkingDirectory = cwd;
            changed = true;
        }

        ImGui.TextColored(ImGuiColors.DalamudGrey, "This decides what Claude can touch. The panel's button lists the repositories under your home.");

        var model = config.ClaudeModel;
        if (ImGui.InputTextWithHint("Model", "account default", ref model, 128))
        {
            config.ClaudeModel = model;
            changed = true;
        }

        var executable = config.ClaudeExecutable;
        if (ImGui.InputTextWithHint("claude executable", "claude", ref executable, 512))
        {
            config.ClaudeExecutable = executable;
            changed = true;
        }

        var extra = config.ClaudeExtraArgs;
        if (ImGui.InputTextWithHint("Extra arguments", "appended verbatim", ref extra, 512))
        {
            config.ClaudeExtraArgs = extra;
            changed = true;
        }

        var partials = config.ClaudeStreamPartials;
        if (ImGui.Checkbox("Stream replies as they are written", ref partials))
        {
            config.ClaudeStreamPartials = partials;
            changed = true;
        }

        ImGui.Separator();
        ImGui.TextColored(ImGuiColors.DalamudGrey, "Permissions");
        ImGui.TextWrapped(claude.PermissionSummary);

        var mode = config.ClaudePermissionMode;
        if (ImGui.InputTextWithHint("Fallback permission mode", "manual", ref mode, 64))
        {
            config.ClaudePermissionMode = mode;
            changed = true;
        }

        ImGui.TextColored(ImGuiColors.DalamudGrey, "Used only when prompts cannot be shown in game. Anything that would still have prompted is denied.");

        var always = config.ClaudeAlwaysAllow;
        ImGui.TextUnformatted(always.Count == 0 ? "No tool is set to always allow." : "Always allowed: " + string.Join(", ", always));
        if (always.Count > 0 && ImGui.Button("Forget them"))
        {
            claude.Rules.ClearAlways();
            config.ClaudeAlwaysAllow = [];
            changed = true;
        }

        ImGui.Separator();
        ImGui.TextColored(ImGuiColors.DalamudGrey, "NPC");
        var npc = config.ClaudeNpc;
        if (ImGui.Checkbox("Summon an NPC beside me for each session", ref npc))
        {
            config.ClaudeNpc = npc;
            changed = true;
        }

        var ask = config.ClaudeAskForLook;
        if (ImGui.Checkbox("Ask the model once for its own name and look", ref ask))
        {
            config.ClaudeAskForLook = ask;
            changed = true;
        }

        if (!claude.NpcAvailable)
            ImGui.TextColored(ImGuiColors.DalamudGrey, "Nothing here can put an avatar in the world yet; the panel works without one.");

        if (config.ClaudeSessions.Count > 0)
        {
            ImGui.Separator();
            ImGui.TextColored(ImGuiColors.DalamudGrey, $"{config.ClaudeSessions.Count} remembered sessions");
            if (ImGui.Button("Forget all remembered sessions"))
            {
                config.ClaudeSessions = [];
                changed = true;
            }
        }

        if (changed)
            desktop.Save();
    }

    private void General()
    {
        var changed = false;
        var b = config.PaletteCloseOnFocusLoss;
        if (ImGui.Checkbox("Close the launcher when it loses focus", ref b))
        {
            config.PaletteCloseOnFocusLoss = b;
            changed = true;
        }

        b = config.CloseOnLaunch;
        if (ImGui.Checkbox("Close the app grid after a launch", ref b))
        {
            config.CloseOnLaunch = b;
            changed = true;
        }

        b = config.NotifyOpened;
        if (ImGui.Checkbox("Notify when a window panel opens", ref b))
        {
            config.NotifyOpened = b;
            changed = true;
        }

        b = config.NotifyEnded;
        if (ImGui.Checkbox("Notify when a window panel ends", ref b))
        {
            config.NotifyEnded = b;
            changed = true;
        }

        ImGui.Separator();
        ImGui.TextDisabled("Terminals (Super+Enter and Terminal=true apps, through terminal.new)");
        var profile = config.TerminalProfile;
        ImGui.SetNextItemWidth(160);
        if (ImGui.InputTextWithHint("Profile", "ghostty's default", ref profile, 64))
        {
            config.TerminalProfile = profile.Trim();
            changed = true;
        }

        var tpin = config.TerminalPin;
        ImGui.SetNextItemWidth(160);
        if (ImGui.InputText("Pin", ref tpin, 64) && tpin.Trim().Length > 0)
        {
            config.TerminalPin = tpin.Trim();
            changed = true;
        }

        ImGui.TextDisabled("Older ghostty-dalamud without terminal.new: this /term line instead");
        var line = config.TerminalLine;
        ImGui.SetNextItemWidth(260);
        if (ImGui.InputText("##terminal", ref line, 200) && line.Trim().Length > 0)
        {
            config.TerminalLine = line.Trim();
            changed = true;
        }

        ImGui.SameLine();
        if (ImGui.SmallButton("new"))
        {
            config.TerminalLine = "new";
            changed = true;
        }

        ImGui.SameLine();
        if (ImGui.SmallButton("pin pet"))
        {
            config.TerminalLine = "pin pet";
            changed = true;
        }

        ImGui.TextWrapped("\"new\" opens a tab in ghostty's dropdown. \"pin pet\" makes a new world terminal that follows you, but only while no dropdown tab is active and no window panel has focus (otherwise ghostty pins that instead).");

        ImGui.Separator();
        ImGui.TextDisabled("Pin arguments (as /term pin takes them)");
        var pin = config.PinArgs;
        ImGui.SetNextItemWidth(160);
        if (ImGui.InputText("Pet → pin (toggle)", ref pin, 64) && pin.Trim().Length > 0)
        {
            config.PinArgs = pin.Trim();
            changed = true;
        }

        var front = config.PinFrontArgs;
        ImGui.SetNextItemWidth(160);
        if (ImGui.InputText("Pin in front", ref front, 64) && front.Trim().Length > 0)
        {
            config.PinFrontArgs = front.Trim();
            changed = true;
        }

        ImGui.TextWrapped("ghostty has no \"pin where it is now\": \"here\" pins 2.5 yalms in front of you. Others: \"me\" (follows, in front), \"target\", \"orbit 3.5\", \"at ANGLE DIST HEIGHT\".");

        if (changed)
            desktop.Save();
    }

    private void Keys()
    {
        var enabled = config.KeybindsEnabled;
        if (ImGui.Checkbox("Key chords on", ref enabled))
        {
            config.KeybindsEnabled = enabled;
            desktop.Save();
        }

        var global = config.ReadKeysGlobally;
        if (ImGui.Checkbox("Read keys globally (also while a window panel has the keyboard)", ref global))
        {
            config.ReadKeysGlobally = global;
            desktop.Save();
        }

        ImGui.TextWrapped("Chords never fire while an ImGui text field has focus. Under Wine the desktop may take Super first (sway binds $mod+d, GNOME opens the overview): unbind it there, or pick Alt+Shift or Ctrl+Alt below.");

        var preset = (int)config.ModifierPreset;
        ImGui.SetNextItemWidth(160);
        ImGui.Combo("Modifier", ref preset, ["Super", "Alt+Shift", "Ctrl+Alt"], 3);
        ImGui.SameLine();
        if (ImGui.Button("Reset all to this modifier"))
        {
            config.ModifierPreset = (ModifierPreset)preset;
            config.Keybinds = Keybinds.Defaults(config.ModifierPreset);
            editing.Clear();
            keybinds.Recompile();
            desktop.Save();
        }
        else if (preset != (int)config.ModifierPreset)
        {
            config.ModifierPreset = (ModifierPreset)preset;
            desktop.Save();
        }

        foreach (var (a, b, chord) in keybinds.Conflicts)
            ImGui.TextColored(ImGuiColors.DalamudOrange, $"{chord} is bound twice: {KeyAction.Describe(a)} / {KeyAction.Describe(b)}");
        foreach (var e in keybinds.Errors)
            ImGui.TextColored(ImGuiColors.DalamudOrange, e);
        if (keybinds.LastFired is { } last)
            ImGui.TextDisabled($"Last chord: {KeyAction.Describe(last.Action)} at {last.At:HH:mm:ss}");

        ImGui.Separator();
        if (!ImGui.BeginTable("##keys", 2, ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY | ImGuiTableFlags.SizingStretchProp, new Vector2(0, ImGui.GetContentRegionAvail().Y)))
            return;
        ImGui.TableSetupColumn("Action", ImGuiTableColumnFlags.WidthStretch, 1.4f);
        ImGui.TableSetupColumn("Chord", ImGuiTableColumnFlags.WidthStretch, 1f);
        foreach (var action in KeyAction.All)
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(KeyAction.Describe(action));
            ImGui.TableNextColumn();
            if (!editing.TryGetValue(action, out var text))
                text = config.Keybinds.GetValueOrDefault(action, "");
            ImGui.SetNextItemWidth(-1);
            var valid = text.Trim().Length == 0 || KeyChord.TryParse(text, out _);
            if (!valid)
                ImGui.PushStyleColor(ImGuiCol.Text, ImGuiColors.DalamudOrange);
            if (ImGui.InputTextWithHint("##" + action, "unbound", ref text, 64))
                editing[action] = text;
            if (!valid)
                ImGui.PopStyleColor();
            if (ImGui.IsItemDeactivatedAfterEdit() && editing.Remove(action, out var edited))
            {
                var next = new Dictionary<string, string>(config.Keybinds);
                if (edited.Trim().Length == 0)
                    next[action] = "";
                else if (KeyChord.TryParse(edited, out var chord))
                    next[action] = chord.ToString();
                else
                    continue;
                config.Keybinds = next;
                keybinds.Recompile();
                desktop.Save();
            }
        }

        ImGui.EndTable();
    }

    private void WindowsTab()
    {
        var w = session.Windows;
        ImGui.TextUnformatted($"Launch backend: {desktop.Backend.Name}{(w.Extended ? " (v1.1 methods)" : "")}");
        ImGui.TextUnformatted($"App list: {desktop.Catalog.Source}, {desktop.Catalog.Catalog.Apps.Count} apps (directory scan: {desktop.Catalog.Scanned.Apps.Count})");
        if (w.Extended && ImGui.SmallButton("Ask the agent for its apps again"))
            w.RefreshAgentLists();
        ImGui.TextUnformatted(w.WindowsAvailable ? "Window IPC: GhosttyDalamud.v1.Call registered" : "Window IPC: not registered (window list, workspaces and window actions are off)");
        if (w.Agent is { } a)
            ImGui.TextUnformatted($"Agent: {(a.Connected ? "connected" : "not connected")}, protocol {a.Version}, windows {(a.WindowsOk ? "yes" : "no")}{(a.Agent.Length > 0 ? " · " + a.Agent : "")}");
        if (session.LastMessage is { } m)
            ImGui.TextDisabled("Last: " + m);

        ImGui.Separator();
        var hide = config.WorkspacesHide;
        if (ImGui.Checkbox("Hide other workspaces' panels", ref hide))
        {
            config.WorkspacesHide = hide;
            desktop.Save();
            session.ResetVisibility();
        }

        ImGui.TextWrapped("Hiding uses window.place with \"hide\" (/term pin hide). Off: workspaces only group panels in the palette and the taskbar.");

        ImGui.TextUnformatted("Workspace:");
        for (var n = 1; n <= WorkspaceModel.Count; n++)
        {
            ImGui.SameLine();
            var current = n == session.CurrentWorkspace;
            if (current)
                ImGui.PushStyleColor(ImGuiCol.Button, ImGui.GetColorU32(ImGuiCol.ButtonActive));
            if (ImGui.SmallButton(n.ToString()))
                session.SwitchWorkspace(n);
            if (current)
                ImGui.PopStyleColor();
        }

        ImGui.Separator();
        if (!ImGui.BeginTable("##windows", 5, ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY | ImGuiTableFlags.SizingStretchProp, new Vector2(0, ImGui.GetContentRegionAvail().Y)))
            return;
        ImGui.TableSetupColumn("Id", ImGuiTableColumnFlags.WidthFixed, 40);
        ImGui.TableSetupColumn("Window", ImGuiTableColumnFlags.WidthStretch, 2);
        ImGui.TableSetupColumn("State", ImGuiTableColumnFlags.WidthStretch, 1);
        ImGui.TableSetupColumn("WS", ImGuiTableColumnFlags.WidthFixed, 30);
        ImGui.TableSetupColumn("", ImGuiTableColumnFlags.WidthFixed, 150);
        ImGui.TableHeadersRow();
        foreach (var p in w.Snapshot.Windows)
        {
            ImGui.TableNextRow();
            ImGui.PushID((int)p.Id);
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(p.Id.ToString());
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(p.DisplayName + (p.Focused ? "  (focused)" : ""));
            ImGui.TableNextColumn();
            ImGui.TextDisabled($"{p.State} {p.Kind}{(session.IsHiddenNow(p) ? " hidden" : "")}");
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(session.WorkspaceOf(p.Id) is var ws and > 0 ? ws.ToString() : "-");
            ImGui.TableNextColumn();
            if (!p.IsEnded)
            {
                if (ImGui.SmallButton("focus"))
                    session.Focus(p.Id);
                ImGui.SameLine();
                if (ImGui.SmallButton(p.IsPet ? "pin" : "pet"))
                    session.TogglePet(p);
                ImGui.SameLine();
                if (ImGui.SmallButton("close"))
                    session.Close(p.Id);
            }

            ImGui.PopID();
        }

        ImGui.EndTable();
    }
}
