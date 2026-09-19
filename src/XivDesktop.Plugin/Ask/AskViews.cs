using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;

namespace XivDesktop.Plugin.Ask;

/// <summary>The dialogue as <see cref="AskService"/> drives it: KamiToolKit's native addon, or the ImGui fallback.</summary>
public interface IAskView
{
    bool IsOpen { get; }

    Action<string>? Submitted { get; set; }

    Action? Dismissed { get; set; }

    void Open();

    void Close();

    void SetSpeaker(string name);

    void Show(IReadOnlyList<(string Label, string Text, bool IsUser)> entries, int lastVisible, bool showArrow);
}

/// <summary><see cref="TalkAddon"/> behind <see cref="IAskView"/>.</summary>
public sealed class NativeTalkView : IAskView, IDisposable
{
    private readonly TalkAddon addon = TalkAddon.Create();
    private string speaker = "";
    private bool closing;

    public NativeTalkView()
    {
        addon.Submitted = s => Submitted?.Invoke(s);
        addon.Dismissed = () =>
        {
            if (!closing)
                Dismissed?.Invoke();
        };
    }

    public bool IsOpen => addon.IsOpen;

    public Action<string>? Submitted { get; set; }

    public Action? Dismissed { get; set; }

    public void Open()
    {
        if (!addon.IsOpen)
            addon.Open();
        else
            addon.FocusInput();
    }

    public void Close()
    {
        if (!addon.IsOpen)
            return;
        closing = true;
        try
        {
            addon.Close();
        }
        finally
        {
            closing = false;
        }
    }

    public void SetSpeaker(string name)
    {
        speaker = name;
        addon.SetSpeaker(name);
    }

    public void Show(IReadOnlyList<(string Label, string Text, bool IsUser)> entries, int lastVisible, bool showArrow)
    {
        if (!addon.IsOpen)
            return;
        addon.SetSpeaker(speaker);
        addon.Show(entries, lastVisible, showArrow);
    }

    public void Dispose() => addon.Dispose();
}

/// <summary>
/// Fallback when KamiToolKit cannot be used: an ImGui window that draws the game's own Talk balloon
/// texture (ITextureProvider.GetFromGame) behind the text so it still looks native. It keeps the game
/// cursor (NoMouseCursorChange while hovered, never SetMouseCursor) and takes keys only while its input
/// has focus.
/// </summary>
public sealed class ImGuiTalkWindow : Window, IAskView
{
    private readonly ITextureProvider textures;
    private IReadOnlyList<(string Label, string Text, bool IsUser)> entries = [];
    private int lastVisible;
    private bool arrow;
    private string speaker = "";
    private string input = "";
    private bool focus;
    private bool setCursorFlag;

    public ImGuiTalkWindow(ITextureProvider textures)
        : base("###XivDesktopAskFallback", ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoBackground | ImGuiWindowFlags.NoSavedSettings)
    {
        this.textures = textures;
        Size = new Vector2(TalkAddon.Width, TalkAddon.Height);
        SizeCondition = ImGuiCond.Always;
        RespectCloseHotkey = false;
    }

    public Action<string>? Submitted { get; set; }

    public Action? Dismissed { get; set; }

    public void Open()
    {
        IsOpen = true;
        focus = true;
    }

    public void Close() => IsOpen = false;

    public void SetSpeaker(string name) => speaker = name;

    public void Show(IReadOnlyList<(string Label, string Text, bool IsUser)> e, int visible, bool showArrow)
    {
        entries = e;
        lastVisible = visible;
        arrow = showArrow;
    }

    public override void PreDraw()
    {
        var vp = ImGui.GetMainViewport();
        Position = new Vector2(vp.Pos.X + (vp.Size.X - TalkAddon.Width) / 2f, vp.Pos.Y + vp.Size.Y * 0.94f - TalkAddon.Height);
        PositionCondition = ImGuiCond.Always;
    }

    public override void Draw()
    {
        var dl = ImGui.GetWindowDrawList();
        var origin = ImGui.GetWindowPos();
        var size = ImGui.GetWindowSize();
        var tex = textures.GetFromGame(TalkStyle.BalloonTexture).GetWrapOrDefault();
        if (tex is not null)
            dl.AddImage(tex.Handle, origin + new Vector2(0, 30), origin + size);
        else
            dl.AddRectFilled(origin + new Vector2(0, 30), origin + size, 0xF0F4F4F0, 18f);

        ImGui.SetCursorPos(new Vector2(30, 4));
        ImGui.TextColored(TalkStyle.NameColor, speaker);

        ImGui.SetCursorPos(new Vector2(30, 48));
        if (ImGui.BeginChild("##asklines", new Vector2(size.X - 60, size.Y - 110), false))
        {
            for (var i = 0; i < entries.Count; i++)
            {
                var (label, text, isUser) = entries[i];
                var last = i == entries.Count - 1;
                var shown = last && !isUser ? text[..Math.Clamp(lastVisible, 0, text.Length)] : text;
                var colour = isUser ? TalkStyle.UserColor : last ? TalkStyle.BodyColor : TalkStyle.OldColor;
                ImGui.PushStyleColor(ImGuiCol.Text, colour);
                ImGui.TextWrapped(isUser || !last ? $"{label}: {shown}" : shown);
                ImGui.PopStyleColor();
            }

            if (arrow)
                ImGui.TextColored(TalkStyle.BodyColor, "▼");
            ImGui.SetScrollHereY(1f);
        }

        ImGui.EndChild();

        ImGui.SetCursorPos(new Vector2(30, size.Y - 48));
        ImGui.SetNextItemWidth(size.X - 60);
        if (focus)
        {
            ImGui.SetKeyboardFocusHere();
            focus = false;
        }

        if (ImGui.InputTextWithHint("##askinput", $"Ask {speaker}…", ref input, 500, ImGuiInputTextFlags.EnterReturnsTrue))
        {
            var q = input.Trim();
            input = "";
            focus = true;
            Submitted?.Invoke(q);
        }

        if (ImGui.IsItemActive() && ImGui.IsKeyPressed(ImGuiKey.Escape))
            Dismissed?.Invoke();

        // Keep the game's cursor while hovering (the user's rule): set the flag only while hovered, clear it only if we set it.
        var io = ImGui.GetIO();
        var hovered = ImGui.IsWindowHovered(ImGuiHoveredFlags.RootAndChildWindows | ImGuiHoveredFlags.AllowWhenBlockedByActiveItem);
        if (hovered && (io.ConfigFlags & ImGuiConfigFlags.NoMouseCursorChange) == 0)
        {
            io.ConfigFlags |= ImGuiConfigFlags.NoMouseCursorChange;
            setCursorFlag = true;
        }
        else if (!hovered && setCursorFlag)
        {
            io.ConfigFlags &= ~ImGuiConfigFlags.NoMouseCursorChange;
            setCursorFlag = false;
        }
    }

    public override void OnClose()
    {
        if (setCursorFlag)
        {
            var io = ImGui.GetIO();
            io.ConfigFlags &= ~ImGuiConfigFlags.NoMouseCursorChange;
            setCursorFlag = false;
        }
    }
}
