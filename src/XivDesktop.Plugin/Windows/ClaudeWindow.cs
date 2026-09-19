using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using XivDesktop.Core.Claude;
using XivDesktop.Plugin.Claude;

namespace XivDesktop.Plugin.Windows;

/// <summary>
/// The Claude panel: the conversation as game UI rather than terminal text. Prompts and replies are
/// rows; every tool call is a compact card with an icon for its kind, the file or command it names, a
/// spinner while it runs and a result line when it is done, expandable to a few lines of detail. Long
/// output is collapsed, and the raw toggle shows exactly what a terminal would have printed.
/// </summary>
/// <remarks>
/// Keyboard first: Enter sends, Shift+Enter is a newline, Esc closes the panel (which hides the session,
/// it keeps running), Ctrl+R toggles raw, Ctrl+C interrupts. The game's own cursor is kept while the
/// pointer is over it — the window system does that for every XivDesktop window.
/// </remarks>
public sealed class ClaudeWindow : Window
{
    private static readonly Vector4 Accent = new(0.60f, 0.50f, 1.00f, 1f);
    private static readonly Vector4 Muted = new(0.62f, 0.64f, 0.70f, 1f);
    private static readonly Vector4 Good = new(0.45f, 0.80f, 0.55f, 1f);
    private static readonly Vector4 Bad = new(0.92f, 0.45f, 0.42f, 1f);
    private static readonly Vector4 Waiting = new(0.95f, 0.78f, 0.35f, 1f);
    private static readonly char[] Spinner = ['|', '/', '-', '\\'];

    private readonly ClaudeService claude;
    private readonly Configuration config;
    private readonly Action save;

    private string input = "";
    private bool focusInput;
    private bool scrollToEnd;
    private bool pickingDirectory;
    private List<WorkingDirectoryChoice>? directories;
    private string? flash;

    public ClaudeWindow(ClaudeService claude, Configuration config, Action save)
        : base("Claude###XivDesktopClaude", ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse)
    {
        this.claude = claude;
        this.config = config;
        this.save = save;
        RespectCloseHotkey = false;
        DisableWindowSounds = true;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(460, 300),
            MaximumSize = new Vector2(1400, 1200),
        };
        Size = new Vector2(680, 520);
        SizeCondition = ImGuiCond.FirstUseEver;
    }

    public void Show()
    {
        IsOpen = true;
        focusInput = true;
    }

    public override void OnOpen() => focusInput = true;

    public override void Draw()
    {
        var session = claude.Active;
        Header(session);
        if (session == null)
        {
            Empty();
            return;
        }

        ImGui.Separator();
        var footer = (ImGui.GetTextLineHeightWithSpacing() * 2) + (ImGui.GetStyle().ItemSpacing.Y * 3);
        if (ImGui.BeginChild("##claude-body", new Vector2(0, -footer), false))
        {
            if (session.ShowRaw)
                Raw(session);
            else
                Transcript(session);
            if (scrollToEnd)
            {
                ImGui.SetScrollHereY(1f);
                scrollToEnd = false;
            }
        }

        ImGui.EndChild();
        ImGui.Separator();
        Footer(session);
        Keys(session);
    }

    private void Header(ClaudeSession? session)
    {
        if (claude.Sessions.Count > 0 && ImGui.BeginTabBar("##claude-sessions", ImGuiTabBarFlags.AutoSelectNewTabs))
        {
            foreach (var s in claude.Sessions)
            {
                var label = $"{s.Look.Name}{(s.Busy ? " *" : "")}###claude-tab-{s.Key}";
                if (ImGui.BeginTabItem(label))
                {
                    if (claude.Active != s)
                        claude.Focus(s.Key);
                    ImGui.EndTabItem();
                }
            }

            ImGui.EndTabBar();
        }

        if (session == null)
            return;

        ImGui.TextColored(Accent, session.Look.Nameplate);
        ImGui.SameLine();
        var model = session.Conversation.Model.Length > 0 ? SessionRecord.ShortModel(session.Conversation.Model) : "default model";
        ImGui.TextColored(Muted, $"· {model}");

        var totals = session.Conversation.Totals.Summary();
        if (totals.Length > 0)
        {
            ImGui.SameLine();
            ImGui.TextColored(Muted, "· " + totals);
        }

        // The working directory decides what Claude can touch, so it is always on screen.
        var cwd = session.Conversation.Cwd.Length > 0 ? session.Conversation.Cwd : session.Record.Cwd;
        if (ImGui.SmallButton(WorkingDirectoryChoice.Shorten(claude.Home, cwd) + "###claude-cwd"))
        {
            pickingDirectory = true;
            directories = null;
        }

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip($"Where this session runs: {cwd}\nClick to choose another (new sessions only).");

        if (!claude.PromptToolAvailable)
        {
            ImGui.SameLine();
            ImGui.TextColored(Waiting, "⚠");
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(claude.PermissionSummary);
        }

        if (config.ClaudeNpc && (!claude.NpcAvailable || claude.NpcNote.Length > 0))
        {
            ImGui.SameLine();
            ImGui.TextColored(Muted, "(no NPC)");
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(claude.NpcNote.Length > 0 ? claude.NpcNote : "No avatar; the panel works without one.");
        }

        DirectoryPicker();
    }

    private void Empty()
    {
        ImGui.Spacing();
        ImGui.TextColored(Muted, claude.Status()[(claude.Status().IndexOf(':') + 1)..].Trim());
        ImGui.Spacing();
        if (ImGui.Button("New session"))
            Flash(claude.New("", ""));
        ImGui.SameLine();
        if (ImGui.Button("Settings"))
            Flash("ok: /desktop settings");

        var remembered = claude.Remembered;
        if (remembered.Count == 0)
            return;
        ImGui.Spacing();
        ImGui.TextColored(Muted, "Recent sessions");
        foreach (var record in remembered.Take(8))
        {
            if (ImGui.Selectable($"{record.Key}###claude-recent-{record.Key}"))
                Flash(claude.Resume(record.Key));
            ImGui.SameLine();
            ImGui.TextColored(Muted, record.Summary(DateTimeOffset.UtcNow));
        }

        FlashLine();
    }

    private void Transcript(ClaudeSession session)
    {
        foreach (var item in session.Conversation.Items)
        {
            switch (item.Kind)
            {
                case ItemKind.Prompt:
                    ImGui.TextColored(Accent, "▸ " + item.Text);
                    break;
                case ItemKind.Reply:
                    ImGui.TextWrapped(item.Text);
                    break;
                case ItemKind.Thinking:
                    Collapsible(item, "thinking", Muted);
                    break;
                case ItemKind.Notice:
                    ImGui.TextColored(item.IsError ? Bad : Muted, item.Text);
                    break;
                case ItemKind.Summary:
                    ImGui.TextColored(Muted, item.Text.Length > 0 ? "— " + item.Text : "—");
                    break;
                case ItemKind.Tool:
                    Card(item);
                    break;
            }

            ImGui.Spacing();
        }
    }

    /// <summary>One tool call: icon, target, status, result, and a few lines of detail when opened.</summary>
    private void Card(ConversationItem item)
    {
        var (colour, status) = item.Status switch
        {
            ToolStatus.Running => (Waiting, Spin()),
            ToolStatus.Asking => (Waiting, "?"),
            ToolStatus.Done => (Good, "✓"),
            ToolStatus.Failed => (Bad, "✗"),
            _ => (Bad, "⊘"),
        };

        ImGui.PushStyleColor(ImGuiCol.ChildBg, new Vector4(1f, 1f, 1f, 0.035f));
        var height = ImGui.GetTextLineHeightWithSpacing() + (ImGui.GetStyle().FramePadding.Y * 2);
        if (item.Expanded && item.Detail.Count > 0)
            height += ImGui.GetTextLineHeightWithSpacing() * item.Detail.Count;
        if (ImGui.BeginChild($"##card-{item.ToolUseId}-{item.At.Ticks}", new Vector2(0, height), false))
        {
            ImGui.TextColored(colour, status);
            ImGui.SameLine();
            ImGui.TextColored(Accent, ToolCards.Glyph(item.Tool));
            ImGui.SameLine();
            ImGui.TextUnformatted(item.ToolName);
            if (item.Target.Length > 0)
            {
                ImGui.SameLine();
                ImGui.TextColored(Muted, item.Target);
            }

            if (item.ResultLine.Length > 0)
            {
                ImGui.SameLine();
                ImGui.TextColored(item.Status == ToolStatus.Done ? Good : colour, "· " + item.ResultLine);
            }

            if (item.Detail.Count > 0)
            {
                ImGui.SameLine();
                if (ImGui.SmallButton((item.Expanded ? "▾" : "▸") + $"###expand-{item.ToolUseId}"))
                    item.Expanded = !item.Expanded;
            }

            if (item.Expanded)
            {
                foreach (var line in item.Detail)
                    ImGui.TextColored(Muted, "  " + line);
            }
        }

        ImGui.EndChild();
        ImGui.PopStyleColor();
    }

    private static void Collapsible(ConversationItem item, string label, Vector4 colour)
    {
        if (ImGui.SmallButton((item.Expanded ? "▾ " : "▸ ") + label + $"###think-{item.At.Ticks}"))
            item.Expanded = !item.Expanded;
        if (!item.Expanded)
            return;
        ImGui.PushStyleColor(ImGuiCol.Text, colour);
        ImGui.TextWrapped(item.Text);
        ImGui.PopStyleColor();
    }

    /// <summary>The raw toggle: the job's stdout exactly as a terminal would have shown it.</summary>
    private static void Raw(ClaudeSession session)
    {
        if (ImGui.SmallButton("Copy all###claude-raw-copy"))
            ImGui.SetClipboardText(session.Conversation.RawText());
        ImGui.PushStyleColor(ImGuiCol.Text, Muted);
        foreach (var line in session.Conversation.Raw)
            ImGui.TextUnformatted(line);
        ImGui.PopStyleColor();
    }

    private void Footer(ClaudeSession session)
    {
        var busy = session.Busy;
        ImGui.SetNextItemWidth(-260);
        if (focusInput)
        {
            ImGui.SetKeyboardFocusHere();
            focusInput = false;
        }

        var entered = ImGui.InputTextWithHint("##claude-input", busy ? "working…" : "Ask Claude", ref input, 8192,
            ImGuiInputTextFlags.EnterReturnsTrue);
        if (entered && input.Trim().Length > 0)
        {
            Flash(claude.Send(session.Key, input.Trim()));
            input = "";
            focusInput = true;
            scrollToEnd = true;
        }

        ImGui.SameLine();
        if (busy)
        {
            if (ImGui.Button("Stop"))
                Flash(claude.Stop(session.Key));
        }
        else
        {
            ImGui.BeginDisabled();
            ImGui.Button("Stop");
            ImGui.EndDisabled();
        }

        ImGui.SameLine();
        var raw = session.ShowRaw;
        if (ImGui.Checkbox("raw", ref raw))
        {
            session.ShowRaw = raw;
            scrollToEnd = true;
        }

        ImGui.SameLine();
        if (ImGui.Button("End"))
            Flash(claude.End(session.Key));
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Ends the session for good. Closing this panel only hides it — it keeps running.");

        FlashLine();
    }

    private void Keys(ClaudeSession session)
    {
        if (!ImGui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows))
            return;
        if (ImGui.IsKeyPressed(ImGuiKey.Escape))
            IsOpen = false;
        if (ImGui.GetIO().KeyCtrl && ImGui.IsKeyPressed(ImGuiKey.R))
            session.ShowRaw = !session.ShowRaw;
        if (ImGui.GetIO().KeyCtrl && ImGui.IsKeyPressed(ImGuiKey.C) && session.Busy)
            Flash(claude.Stop(session.Key));
    }

    /// <summary>
    /// The working directory picker: the home directory, then the repositories under it (the directories
    /// with a .git). Choosing one is a setting, so it applies to the next session rather than this one.
    /// </summary>
    private void DirectoryPicker()
    {
        if (pickingDirectory)
        {
            ImGui.OpenPopup("##claude-dirs");
            pickingDirectory = false;
            directories = null;
        }

        if (!ImGui.BeginPopup("##claude-dirs"))
            return;

        directories ??= claude.Directories();
        ImGui.TextColored(Muted, "Where new sessions run");
        ImGui.Separator();
        if (ImGui.BeginChild("##claude-dir-list", new Vector2(420, 320), false))
        {
            foreach (var choice in directories)
            {
                var current = string.Equals(choice.Path, claude.WorkingDirectory(), StringComparison.Ordinal);
                if (ImGui.Selectable(choice.Display + (choice.IsRepo ? "" : "  (home)"), current))
                {
                    config.ClaudeWorkingDirectory = choice.Path;
                    save();
                    Flash($"ok: new sessions run in {choice.Display}");
                    ImGui.CloseCurrentPopup();
                }
            }

            if (directories.Count <= 1)
                ImGui.TextColored(Muted, "No repositories found under the home directory.");
        }

        ImGui.EndChild();
        ImGui.EndPopup();
    }

    private void Flash(string result) => flash = result;

    private void FlashLine()
    {
        if (flash == null)
            return;
        var error = flash.StartsWith("error", StringComparison.Ordinal);
        var text = flash[(flash.IndexOf(':') + 1)..].Trim();
        if (text.Length > 0)
            ImGui.TextColored(error ? Bad : Muted, text);
    }

    private static string Spin() => Spinner[(int)(ImGui.GetTime() * 8) % Spinner.Length].ToString();
}
