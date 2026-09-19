using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using XivDesktop.Core.Claude;
using XivDesktop.Plugin.Claude;

namespace XivDesktop.Plugin.Windows;

/// <summary>
/// The permission dialog: Claude asked to use a tool, and the player decides. It shows what the tool is,
/// what it would touch and every argument, with Allow once / Allow for this session / Always for this
/// tool / Deny. It opens itself whenever a prompt is waiting and closes when it is answered.
/// </summary>
/// <remarks>
/// Nothing here can approve on its own: a prompt that is never answered is denied by the queue's timeout,
/// and when the prompt route is unavailable the panel says so rather than letting anything through.
/// </remarks>
public sealed class ClaudePermissionWindow : Window
{
    private static readonly Vector4 Accent = new(0.60f, 0.50f, 1.00f, 1f);
    private static readonly Vector4 Muted = new(0.62f, 0.64f, 0.70f, 1f);
    private static readonly Vector4 Danger = new(0.92f, 0.45f, 0.42f, 1f);

    private readonly ClaudeService claude;

    public ClaudePermissionWindow(ClaudeService claude)
        : base("Claude needs a decision###XivDesktopClaudePermission",
            ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoSavedSettings)
    {
        this.claude = claude;
        RespectCloseHotkey = false;
        ShowCloseButton = false;
        DisableWindowSounds = true;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(420, 140),
            MaximumSize = new Vector2(760, 640),
        };
    }

    /// <summary>Called each frame: the dialog is open exactly while a prompt is waiting.</summary>
    public void Sync() => IsOpen = claude.Prompts.Current != null;

    public override void Draw()
    {
        var request = claude.Prompts.Current;
        if (request == null)
        {
            IsOpen = false;
            return;
        }

        ImGui.TextColored(Accent, ToolCards.Glyph(request.Kind) + "  " + request.ToolName);
        if (request.SessionKey.Length > 0)
        {
            ImGui.SameLine();
            ImGui.TextColored(Muted, "· " + request.SessionKey);
        }

        if (request.Target.Length > 0)
            ImGui.TextWrapped(request.Target);

        ImGui.Separator();
        if (ImGui.BeginTable("##claude-permission-args", 2, ImGuiTableFlags.SizingStretchProp))
        {
            foreach (var (name, value) in request.Arguments())
            {
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                ImGui.TextColored(Muted, name);
                ImGui.TableNextColumn();
                ImGui.TextWrapped(value);
            }

            ImGui.EndTable();
        }

        ImGui.Separator();
        if (ImGui.Button("Allow once"))
            claude.Answer(PermissionChoice.Once);
        ImGui.SameLine();
        if (ImGui.Button("Allow for this session"))
            claude.Answer(PermissionChoice.Session);
        ImGui.SameLine();
        if (ImGui.Button($"Always allow {request.ToolName}"))
            claude.Answer(PermissionChoice.Always);
        ImGui.SameLine();
        ImGui.PushStyleColor(ImGuiCol.Text, Danger);
        if (ImGui.Button("Deny"))
            claude.Answer(PermissionChoice.Deny);
        ImGui.PopStyleColor();

        var queued = claude.Prompts.Waiting;
        if (queued > 0)
            ImGui.TextColored(Muted, $"{queued} more waiting");
        ImGui.TextColored(Muted, "Enter once · S this session · A always · Esc deny");

        Keys();
    }

    /// <summary>Everything the buttons do is reachable from the keyboard.</summary>
    private void Keys()
    {
        if (!ImGui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows))
            return;
        if (ImGui.IsKeyPressed(ImGuiKey.Enter) || ImGui.IsKeyPressed(ImGuiKey.KeypadEnter))
            claude.Answer(PermissionChoice.Once);
        else if (ImGui.IsKeyPressed(ImGuiKey.S))
            claude.Answer(PermissionChoice.Session);
        else if (ImGui.IsKeyPressed(ImGuiKey.A))
            claude.Answer(PermissionChoice.Always);
        else if (ImGui.IsKeyPressed(ImGuiKey.Escape) || ImGui.IsKeyPressed(ImGuiKey.D))
            claude.Answer(PermissionChoice.Deny);
    }
}
