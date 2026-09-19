// "Windows" toolbar widget: the current workspace number, then a strip of the open window panels (a letter
// tile and the title). Click a panel: focus it. Middle click: close it. Right click: a menu with pin/pet and
// move-to-workspace. Clicking the workspace number opens the same menu for all panels and workspaces.
// Talks to XivDesktop only through XivDesktop.v1.Windows / WindowAction / Workspace.
using System.Reflection;
using Umbra.Common;
using Umbra.Widgets;
using Una.Drawing;
using Una.Drawing.Templating.StyleParser;
using XivDesktop.Core;
using XivDesktop.Shared;

namespace Umbra.XivDesktop;

[ToolbarWidget(
    "XivDesktopWindows",
    "Windows",
    "Taskbar of XivDesktop's window panels with the current workspace. Click: focus, middle click: close, right click: pin/pet/workspace. Needs XivDesktop and ghostty-dalamud's window IPC.",
    ["windows", "taskbar", "workspace", "xivdesktop", "ghostty"])]
public sealed class WindowsWidget(
    WidgetInfo info,
    string? guid = null,
    Dictionary<string, object>? configValues = null
) : StandardToolbarWidget(info, guid, configValues)
{
    private const int MaxItems = 10;
    private const string CvarMaxTitle = "MaxTitleLength";
    private const string CvarAllWorkspaces = "ShowAllWorkspaces";

    private static readonly Stylesheet StripStylesheet = StyleParser.StylesheetFromCode(
        """
        .xd-strip {
            flow: horizontal;
            gap: 4;
            padding: 0 4 0 4;
        }

        .xd-win {
            flow: horizontal;
            gap: 5;
            padding: 3 7 3 7;
            border-radius: 5;
            background-color: "Widget.Background";
            stroke-color: "Widget.Border";
            stroke-width: 1;
        }

        .xd-win:hover {
            background-color: "Widget.BackgroundHover";
            stroke-color: "Widget.BorderHover";
        }

        .xd-win.focused {
            stroke-color: "Widget.TextHover";
        }

        .xd-title.muted {
            color: "Widget.TextMuted";
        }

        .xd-icon {
            size: 16 16;
            border-radius: 4;
            font-size: 11;
            text-align: middle-center;
            color: "Widget.Text";
            outline-color: "Widget.TextOutline";
            outline-size: 1;
        }

        .xd-title {
            size: 0 16;
            font-size: 12;
            text-align: middle-left;
            text-overflow: false;
            word-wrap: false;
            color: "Widget.Text";
            outline-color: "Widget.TextOutline";
            outline-size: 1;
        }
        """);

    private readonly Node _strip = new() { ClassList = new ObservableHashSet<string> { "xd-strip" }, Stylesheet = StripStylesheet };
    private readonly Item[] _items = new Item[MaxItems];
    private XivDesktopClient? _client;

    protected override StandardWidgetFeatures Features => StandardWidgetFeatures.Text;

    private readonly MenuPopup _menuPopup = new();
    private WindowsMenu? _menu;

    public override WidgetPopup Popup => _menuPopup;

    private WindowsMenu Menu => _menu ??= new WindowsMenu(_menuPopup);

    protected override IEnumerable<IWidgetConfigVariable> GetConfigVariables() =>
    [
        .. base.GetConfigVariables(),
        new IntegerWidgetConfigVariable(CvarMaxTitle, "Title length", "Longest title shown on a panel button (characters).", 18, 4, 64),
        new BooleanWidgetConfigVariable(CvarAllWorkspaces, "Show every workspace",
            "Also list panels on other (hidden) workspaces, dimmed. Off: only the current workspace.", false),
    ];

    protected override void OnLoad()
    {
        _client = Framework.Service<XivDesktopClient>();
        for (var i = 0; i < MaxItems; i++)
        {
            var item = new Item(this);
            _items[i] = item;
            _strip.AppendChild(item.Root);
        }

        Node.AppendChild(_strip);
        _menuPopup.OnPopupOpen += Menu.OnOpened;
        _menuPopup.OnPopupClose += Menu.OnClosed;
    }

    protected override void OnUnload()
    {
        _strip.Remove(true);
        _menuPopup.OnPopupOpen -= Menu.OnOpened;
        _menuPopup.OnPopupClose -= Menu.OnClosed;
        _client = null;
    }

    protected override void OnDraw()
    {
        var client = _client;
        if (client is null) return;
        var w = client.Windows;
        if (client.State == DesktopLinkState.Missing || w is null)
        {
            SetText("–");
            SetTooltip("XivDesktop is not loaded.");
            Hide(0);
            return;
        }

        if (!w.Available)
        {
            SetText(w.Workspace.ToString());
            SetTooltip("Window list unavailable: needs ghostty-dalamud's GhosttyDalamud.v1.Call IPC.");
            Hide(0);
            return;
        }

        SetText(w.Workspace.ToString());
        Menu.Refresh(w);
        var all = GetConfigValue<bool>(CvarAllWorkspaces);
        var shown = w.Windows.Where(p => p.State != "ended" && (all || p.Workspace == 0 || p.Workspace == w.Workspace)).Take(MaxItems).ToList();
        var total = w.Windows.Count(p => p.State != "ended");
        SetTooltip($"Workspace {w.Workspace} · {total} window panel{(total == 1 ? "" : "s")}\nClick: all panels and workspaces");

        var max = Math.Clamp(GetConfigValue<int>(CvarMaxTitle), 4, 64);
        for (var i = 0; i < shown.Count; i++)
            _items[i].Show(shown[i], w.Workspace, max);
        Hide(shown.Count);
    }

    private void Hide(int from)
    {
        for (var i = from; i < MaxItems; i++)
            _items[i].Hide();
    }

    /// <summary>Opens the widget's menu for one panel (right click on its button).</summary>
    private void OpenMenuFor(WindowPayload panel)
    {
        Menu.Target = panel;
        if (_client?.Windows is { } w) Menu.Refresh(w, force: true);
        try
        {
            // Umbra opens popups through this event; a widget cannot raise it directly from outside
            // ToolbarWidget, so use its backing field. Unverified against Umbra at runtime.
            var field = typeof(ToolbarWidget).GetField("OpenPopup", BindingFlags.Instance | BindingFlags.NonPublic);
            (field?.GetValue(this) as Action<ToolbarWidget, WidgetPopup>)?.Invoke(this, Popup);
        }
        catch (Exception ex)
        {
            Logger.Warning("[Umbra.XivDesktop] opening the window menu failed: " + ex.Message);
        }
    }

    private void Act(string action, long id, string? pin = null, int workspace = 0)
    {
        try
        {
            _client?.WindowAction(action, id, pin, workspace);
        }
        catch (Exception ex)
        {
            Logger.Warning("[Umbra.XivDesktop] window action failed: " + ex.Message);
        }
    }

    /// <summary>One pooled panel button: a letter tile and the title.</summary>
    private sealed class Item
    {
        private readonly Node _icon = new() { ClassList = new ObservableHashSet<string> { "xd-icon" } };
        private readonly Node _title = new() { ClassList = new ObservableHashSet<string> { "xd-title" } };
        private WindowPayload? _panel;

        public Item(WindowsWidget owner)
        {
            Root = new Node { ClassList = new ObservableHashSet<string> { "xd-win" } };
            Root.AppendChild(_icon);
            Root.AppendChild(_title);
            Root.OnClick += _ =>
            {
                if (_panel is { } p)
                {
                    owner.Menu.SuppressUntil = Environment.TickCount64 + 300; // a click on a panel is not a click on the widget
                    owner.Act("focus", p.Id);
                }
            };
            Root.OnMiddleClick += _ =>
            {
                if (_panel is { } p)
                {
                    owner.Menu.SuppressUntil = Environment.TickCount64 + 300;
                    owner.Act("close", p.Id);
                }
            };
            Root.OnRightClick += _ =>
            {
                if (_panel is { } p) owner.OpenMenuFor(p);
            };
            Root.Style.IsVisible = false;
        }

        public Node Root { get; }

        public void Show(WindowPayload p, int currentWorkspace, int maxTitle)
        {
            _panel = p;
            var name = p.Title.Length > 0 ? p.Title : p.App.Length > 0 ? p.App : $"window {p.Id}";
            var label = name.Length > maxTitle ? name[..(maxTitle - 1)].TrimEnd() + "…" : name;
            if (!Equals(_title.NodeValue, label)) _title.NodeValue = label;
            var letter = LetterTile.Letter(name);
            if (!Equals(_icon.NodeValue, letter)) _icon.NodeValue = letter;
            _icon.Style.BackgroundColor = new Color(LetterTile.Color(p.AppId ?? p.App));
            Root.Tooltip = $"{name}\n{p.Kind}{(p.State == "pending" ? " (opening)" : "")} · workspace {p.Workspace}"
                           + "\nClick: focus · Middle click: close · Right click: more";
            Root.ToggleClass("focused", p.Focused);
            _title.ToggleClass("muted", p.Workspace != 0 && p.Workspace != currentWorkspace);
            Root.Style.IsVisible = true;
        }

        public void Hide()
        {
            _panel = null;
            Root.Style.IsVisible = false;
        }
    }

    /// <summary>
    /// The widget's menu (Umbra's MenuPopup, which is sealed, so it is filled from outside): actions for one
    /// panel after a right click, else every panel and workspace. Rebuilt only when the list changes.
    /// </summary>
    private sealed class WindowsMenu(MenuPopup popup)
    {
        private readonly List<string> _ids = [];
        private string _key = "";

        public WindowPayload? Target { get; set; }

        /// <summary>A click on a panel button also reaches the widget; a menu that opens before this is closed again.</summary>
        public long SuppressUntil { get; set; }

        public void OnOpened()
        {
            if (Environment.TickCount64 < SuppressUntil) popup.Close();
        }

        public void OnClosed()
        {
            if (Target == null) return;
            Target = null;
            _key = "";
        }

        public void Refresh(WindowsPayload w, bool force = false)
        {
            var key = Target is { } t
                ? $"t{t.Id}:{t.Kind}:{t.Workspace}:{t.Title}"
                : $"a{w.Workspace}:" + string.Join(",", w.Windows.Where(p => p.State != "ended").Select(p => $"{p.Id}/{p.Workspace}/{p.Title}"));
            if (!force && key == _key) return;
            _key = key;
            Rebuild(w);
        }

        private void Rebuild(WindowsPayload w)
        {
            foreach (var id in _ids) popup.RemoveById(id, true);
            _ids.Clear();
            var client = Framework.Service<XivDesktopClient>();
            if (!w.Available)
            {
                Add("unavailable", "Window list unavailable", null, disabled: true);
                return;
            }

            if (Target is { } t)
            {
                var name = t.Title.Length > 0 ? t.Title : t.App.Length > 0 ? t.App : $"window {t.Id}";
                Add("focus", "Focus " + name, () => client.WindowAction("focus", t.Id));
                Add("toggle", t.Kind == "pet" ? "Pin here" : "Make pet", () => client.WindowAction(t.Kind == "pet" ? "pin" : "pet", t.Id));
                Add("front", "Pin in front (follows you)", () => client.WindowAction("place", t.Id, "me"));
                for (var n = 1; n <= 9; n++)
                {
                    var ws = n;
                    if (ws != t.Workspace)
                        Add("move" + ws, $"Move to workspace {ws}", () => client.WindowAction("move", t.Id, workspace: ws));
                }

                Add("close", "Close", () => client.WindowAction("close", t.Id));
                return;
            }

            foreach (var p in w.Windows.Where(p => p.State != "ended"))
            {
                var id = p.Id;
                var name = p.Title.Length > 0 ? p.Title : p.App.Length > 0 ? p.App : $"window {p.Id}";
                Add("win" + id, $"{name}  ·  ws {p.Workspace}", () => client.WindowAction("focus", id));
            }

            for (var n = 1; n <= 9; n++)
            {
                var ws = n;
                Add("ws" + ws, ws == w.Workspace ? $"Workspace {ws}  (current)" : $"Workspace {ws}", () => client.SwitchWorkspace(ws), disabled: ws == w.Workspace);
            }
        }

        private void Add(string id, string label, Action? onClick, bool disabled = false)
        {
            var button = new MenuPopup.Button(id) { Label = label, IsDisabled = disabled, ClosePopupOnClick = true };
            if (onClick != null)
            {
                button.OnClick = () =>
                {
                    try
                    {
                        onClick();
                    }
                    catch (Exception ex)
                    {
                        Logger.Warning("[Umbra.XivDesktop] menu action failed: " + ex.Message);
                    }
                };
            }

            popup.Add(button);
            _ids.Add(id);
        }
    }
}
