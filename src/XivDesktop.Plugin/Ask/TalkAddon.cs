using System.Numerics;
using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiToolKit.BaseTypes;
using KamiToolKit.Nodes;
using KamiToolKit.Nodes.Simplified;
using Lumina.Text;
using Lumina.Text.ReadOnly;
using XivDesktop.Core.Ask;

namespace XivDesktop.Plugin.Ask;

/// <summary>
/// The in-game dialogue: a native addon built with KamiToolKit, laid out like the game's Talk window at the
/// bottom centre of the screen. The speaker's name sits above a balloon; earlier turns scroll inside it and
/// the newest answer types out; "▼" shows when it is complete; a native text input takes the follow-up
/// (Enter sends, Esc dismisses). As a native addon it uses the game's own cursor and keyboard focus: keys
/// reach it only while its input is focused, so game hotkeys keep working otherwise.
/// </summary>
public sealed unsafe class TalkAddon : NativeAddon
{
    public const float Width = 760f;
    public const float Height = 262f;
    private const float Pad = 30f;
    private const float Top = 30f; // the name plate overlaps the balloon's top edge
    private const float InputHeight = 30f;

    private readonly List<TextNode> lines = [];
    private SimpleNineGridNode? balloon;
    private SimpleNineGridNode? plate;
    private TextNode? nameNode;
    private ScrollingNode<VerticalListNode>? history;
    private SimpleImageNode? arrow;
    private TextInputNode? input;
    private float arrowClock;
    private string placeholder = "Ask…";
    private bool focusPending;

    /// <summary>Enter in the input: the text to ask (may be empty = advance/skip).</summary>
    public Action<string>? Submitted { get; set; }

    /// <summary>Esc in the input, or the addon was closed by the game (Esc with nothing focused, close-all).</summary>
    public Action? Dismissed { get; set; }

    public static TalkAddon Create() => new()
    {
        InternalName = "XivDesktopAsk",
        Title = new ReadOnlySeString("Ask".AsSpan()),
        Size = new Vector2(Width, Height),
        DisableClamping = false,
        OpenWindowSoundEffectId = 0,
        RememberClosePosition = false,
    };

    protected override void OnSetup(AtkUnitBase* addon, Span<AtkValue> atkValueSpan)
    {
        // The window frame would make this look like a settings window; the balloon replaces it.
        if (WindowNode is not null)
            WindowNode.IsVisible = false;

        balloon = new SimpleNineGridNode
        {
            Position = new Vector2(0, Top),
            Size = new Vector2(Width, Height - Top),
            TexturePath = TalkStyle.BalloonTexture,
            TextureCoordinates = TalkStyle.BalloonUv,
            TextureSize = TalkStyle.BalloonSize,
            TopOffset = TalkStyle.BalloonEdge,
            BottomOffset = TalkStyle.BalloonEdge,
            LeftOffset = TalkStyle.BalloonEdge,
            RightOffset = TalkStyle.BalloonEdge,
            IsVisible = true,
        };
        AddNode(balloon);

        plate = new SimpleNineGridNode
        {
            Position = new Vector2(Pad - 8, 0),
            Size = new Vector2(260, 40),
            TexturePath = TalkStyle.PlateTexture,
            TextureCoordinates = TalkStyle.PlateUv,
            TextureSize = TalkStyle.PlateSize,
            LeftOffset = TalkStyle.PlateEdge,
            RightOffset = TalkStyle.PlateEdge,
            IsVisible = true,
        };
        AddNode(plate);

        nameNode = new TextNode
        {
            Position = new Vector2(Pad + 6, 6),
            Size = new Vector2(240, 28),
            FontType = FontType.TrumpGothic,
            FontSize = 23,
            TextColor = TalkStyle.NameColor,
            TextOutlineColor = TalkStyle.NameOutline,
            TextFlags = TextFlags.Edge,
            AlignmentType = AlignmentType.Left,
            IsVisible = true,
        };
        AddNode(nameNode);

        history = new ScrollingNode<VerticalListNode>
        {
            Position = new Vector2(Pad, Top + 18),
            Size = new Vector2(Width - Pad * 2, Height - Top - 18 - InputHeight - 22),
            AutoHideScrollBar = true,
            ScrollSpeed = 24,
            IsVisible = true,
        };
        history.ContentNode.FitContents = true;
        history.ContentNode.ItemSpacing = 6;
        AddNode(history);

        arrow = new SimpleImageNode
        {
            Position = new Vector2(Width - Pad - 20, Height - InputHeight - 46),
            Size = TalkStyle.ArrowSize,
            TexturePath = TalkStyle.PlateTexture,
            TextureCoordinates = TalkStyle.ArrowUv,
            TextureSize = TalkStyle.ArrowSize,
            IsVisible = false,
        };
        AddNode(arrow);

        input = new TextInputNode
        {
            Position = new Vector2(Pad, Height - InputHeight - 14),
            Size = new Vector2(Width - Pad * 2, InputHeight),
            MaxCharacters = 500,
            PlaceholderString = placeholder,
            IsVisible = true,
            OnInputComplete = text => Submit(text.ExtractText()),
            OnEscapeEntered = () => Dismissed?.Invoke(),
        };
        AddNode(input);

        foreach (var l in lines)
            history.ContentNode.AddNode(l);
        PlaceBottomCentre();
        focusPending = true;
    }

    private void Submit(string text)
    {
        if (input is not null)
            input.String = new ReadOnlySeString("".AsSpan());
        Submitted?.Invoke(text.Trim());
        focusPending = true; // keep typing follow-ups
    }

    protected override void OnUpdate(AtkUnitBase* addon)
    {
        if (focusPending && input is not null)
        {
            focusPending = false;
            FocusInput();
        }

        if (arrow is { IsVisible: true })
        {
            arrowClock += 1f / 60f;
            arrow.Alpha = 0.55f + 0.45f * MathF.Abs(MathF.Sin(arrowClock * 3f));
        }
    }

    protected override void OnFinalize(AtkUnitBase* addon)
    {
        balloon = plate = null;
        nameNode = null;
        arrow = null;
        history = null;
        input = null;
        lines.Clear();
        Dismissed?.Invoke();
    }

    /// <summary>Puts keyboard focus in the text input (after opening and after each question).</summary>
    public void FocusInput()
    {
        if (input is null || !IsOpen)
            return;
        var stage = AtkStage.Instance();
        if (stage != null && stage->AtkInputManager != null)
            stage->AtkInputManager->SetFocus(input.CollisionNode, this, 0);
    }

    private void PlaceBottomCentre()
    {
        var stage = AtkStage.Instance();
        if (stage == null)
            return;
        var w = stage->ScreenSize.Width;
        var h = stage->ScreenSize.Height;
        if (w <= 0 || h <= 0)
            return;
        SetWindowPosition(new Vector2((w - Width) / 2f, h - Height - h * 0.06f));
    }

    public void SetSpeaker(string name)
    {
        placeholder = $"Ask {name}…";
        if (nameNode is not null)
            nameNode.String = Se(name);
        if (plate is not null && nameNode is not null)
            plate.Width = Math.Clamp(nameNode.GetTextDrawSize().X + 44, 140, Width - Pad * 2);
        if (input is not null)
            input.PlaceholderString = placeholder;
    }

    /// <summary>
    /// Shows the conversation: one entry per turn; <paramref name="lastVisible"/> characters of the newest
    /// answer are shown (the typewriter). Existing nodes are reused; only changed text is rewritten.
    /// </summary>
    public void Show(IReadOnlyList<(string Label, string Text, bool IsUser)> entries, int lastVisible, bool showArrow)
    {
        if (history is null)
            return;
        var changed = false;
        while (lines.Count < entries.Count)
        {
            var node = new TextNode
            {
                Size = new Vector2(history.Width - 16, 20),
                FontSize = 15,
                LineSpacing = 20,
                TextFlags = TextFlags.WordWrap | TextFlags.MultiLine,
                AlignmentType = AlignmentType.TopLeft,
                IsVisible = true,
            };
            lines.Add(node);
            history.ContentNode.AddNode(node);
            changed = true;
        }

        while (lines.Count > entries.Count)
        {
            var node = lines[^1];
            lines.RemoveAt(lines.Count - 1);
            history.ContentNode.RemoveNode(node);
            changed = true;
        }

        for (var i = 0; i < entries.Count; i++)
        {
            var (label, text, isUser) = entries[i];
            var visible = i == entries.Count - 1 && !isUser ? text[..Math.Clamp(lastVisible, 0, text.Length)] : text;
            var s = Line(label, visible, isUser, current: i == entries.Count - 1);
            var node = lines[i];
            if (node.String.Equals(s))
                continue;
            node.String = s;
            node.TextColor = isUser ? TalkStyle.UserColor : i == entries.Count - 1 ? TalkStyle.BodyColor : TalkStyle.OldColor;
            var h = Math.Max(20f, node.GetTextDrawSize(false).Y + 4);
            if (Math.Abs(node.Height - h) > 0.5f)
            {
                node.Height = h;
                changed = true;
            }
        }

        if (changed)
        {
            history.ContentNode.RecalculateLayout();
            history.RecalculateSizes();
        }

        history.ScrollToEnd();
        if (arrow is not null)
            arrow.IsVisible = showArrow;
    }

    private static ReadOnlySeString Line(string label, string text, bool isUser, bool current)
    {
        var b = new SeStringBuilder();
        if (label.Length > 0 && (isUser || !current))
            b.Append(label).Append(": ");
        var first = true;
        foreach (var part in Clean(text).Split('\n'))
        {
            if (!first)
                b.AppendNewLine();
            b.Append(part);
            first = false;
        }

        return b.ToReadOnlySeString();
    }

    private static string Clean(string s) => s.Replace('\u0002', ' ').Replace('\u0003', ' ').Replace("\r", "", StringComparison.Ordinal);

    private static ReadOnlySeString Se(string s) => new(Clean(s).AsSpan());
}

/// <summary>
/// Textures and colours for the dialogue, taken from the game's own Talk window: ui/uld/Talk.uld names
/// Talk_Basic.tex (the balloon, three 544x144 variants) and Talk.tex (the 288x36 name plate and the two
/// 16x24 advance arrows at u=288 and u=306). The game picks the high-resolution (_hr1) variants itself.
/// </summary>
internal static class TalkStyle
{
    public const string BalloonTexture = "ui/uld/Talk_Basic.tex";
    public static readonly Vector2 BalloonUv = new(0, 0);
    public static readonly Vector2 BalloonSize = new(544, 144);
    public const int BalloonEdge = 28;

    public const string PlateTexture = "ui/uld/Talk.tex";
    public static readonly Vector2 PlateUv = new(0, 0);
    public static readonly Vector2 PlateSize = new(288, 36);
    public const int PlateEdge = 24;

    /// <summary>The "▼" advance arrow part of Talk.tex.</summary>
    public static readonly Vector2 ArrowUv = new(288, 0);

    public static readonly Vector2 ArrowSize = new(16, 24);

    public static readonly Vector4 NameColor = new(1f, 1f, 1f, 1f);
    public static readonly Vector4 NameOutline = new(0.33f, 0.22f, 0.07f, 1f);
    public static readonly Vector4 BodyColor = new(0.1f, 0.1f, 0.1f, 1f);
    public static readonly Vector4 OldColor = new(0.33f, 0.33f, 0.33f, 1f);
    public static readonly Vector4 UserColor = new(0.16f, 0.3f, 0.5f, 1f);
}
