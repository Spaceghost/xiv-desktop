// Compact launcher popup (Una.Drawing nodes): search box, then either search results or the
// favourites and recents. Button nodes are pooled; each frame only changes what differs.
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Umbra.Common;
using Umbra.Widgets;
using Umbra.Windows.Components;
using Una.Drawing;
using Una.Drawing.Templating.StyleParser;
using XivDesktop.Core;
using XivDesktop.Shared;

namespace Umbra.XivDesktop;

public sealed class AppsPopup : WidgetPopup
{
    private const int MaxResults = 10;
    private const int MaxFavourites = 10;
    private const int MaxRecents = 6;

    private static readonly Stylesheet PopupStylesheet = StyleParser.StylesheetFromCode(
        """
        @import "globals";

        .apps-popup {
            flow: vertical;
            size: 320 0;
            padding: 10;
            gap: 8;
        }

        .muted {
            size: 300 0;
            font-size: 11;
            color: "Widget.PopupMenuTextMuted";
            outline-color: "Widget.PopupMenuTextOutline";
            outline-size: 1;
            word-wrap: true;
        }

        .section {
            flow: vertical;
            auto-size: grow fit;
            gap: 3;
        }

        .section-title {
            auto-size: grow fit;
            font-size: 12;
            color: "Widget.PopupMenuTextMuted";
            outline-color: "Widget.PopupMenuTextOutline";
            outline-size: 1;
            padding: 0 0 3 0;
            border-color: "Widget.Border";
            border-width: 0 0 1 0;
        }

        .list {
            flow: vertical;
            auto-size: grow fit;
            gap: 3;
        }

        .footer {
            flow: horizontal;
            auto-size: grow fit;
            gap: 6;
        }
        """);

    private readonly XivDesktopClient _client = Framework.Service<XivDesktopClient>();
    private readonly StringInputNode _search;
    private readonly Node _message;
    private readonly Section _results;
    private readonly Section _favourites;
    private readonly Section _recents;
    private readonly ButtonNode _openButton;
    private string _query = "";
    private string _lastQuery = "\0";
    private IReadOnlyList<AppInfo>? _lastApps;
    private string? _flash;
    private long _flashUntil;

    public AppsPopup()
    {
        _search = new StringInputNode("search", "", 64, "", "", 0, immediate: true);
        _search.OnValueChanged += v => _query = v ?? "";

        _message = N("muted");
        _results = new Section("Results", MaxResults, Launch);
        _favourites = new Section("Favourites", MaxFavourites, Launch);
        _recents = new Section("Recent", MaxRecents, Launch);

        _openButton = new ButtonNode("open", "Open launcher", FontAwesomeIcon.ThLarge, isGhost: true, isSmall: true);
        _openButton.OnClick += _ => Safe(() =>
        {
            if (_client.OpenLauncher(_query)) Close();
        });
        var footer = N("footer");
        footer.AppendChild(_openButton);

        Node = new Node
        {
            ClassList = new ObservableHashSet<string> { "apps-popup" },
            Stylesheet = PopupStylesheet,
        };
        Node.AppendChild(_search);
        Node.AppendChild(_message);
        Node.AppendChild(_results.Root);
        Node.AppendChild(_favourites.Root);
        Node.AppendChild(_recents.Root);
        Node.AppendChild(footer);
    }

    protected override Node Node { get; }

    protected override void OnClose() => GameCursor.Update("apps", false);

    protected override void OnOpen()
    {
        _client.Invalidate();
        _flash = null;
    }

    protected override void OnUpdate()
    {
        GameCursor.Update("apps", IsOpen && Node.Bounds.MarginRect.Contains(ImGui.GetMousePos()));
        var state = _client.State;
        var ready = state == DesktopLinkState.Ready;
        var now = Environment.TickCount64;

        var message = _flash != null && now < _flashUntil
            ? _flash
            : state switch
            {
                DesktopLinkState.Missing => "XivDesktop is not loaded. Enable it in /xlplugins; this widget reconnects automatically.",
                DesktopLinkState.NoGhostty => _client.Status?.Summary ?? "needs ghostty-dalamud",
                _ => _client.Apps.Count == 0 ? (_client.Status?.Summary ?? "no apps") : null,
            };
        _message.Style.IsVisible = message != null;
        if (message != null) _message.NodeValue = message;

        _search.Style.IsVisible = state != DesktopLinkState.Missing;
        _openButton.IsDisabled = state == DesktopLinkState.Missing;

        var apps = _client.Apps;
        var searching = _query.Trim().Length > 0;
        if (searching && (_query != _lastQuery || !ReferenceEquals(apps, _lastApps)))
        {
            var favs = _client.Summaries.Where(s => s.Favourite).Select(s => s.Id).ToList();
            var recents = _client.Summaries.Where(s => s.Recent != null).OrderBy(s => s.Recent).Select(s => s.Id).ToList();
            _results.Show(AppSearch.Search(apps, _query, favs, recents).Take(MaxResults).Select(a => Find(a.Id)).OfType<AppSummary>(), ready);
        }

        _lastQuery = _query;
        _lastApps = apps;
        _results.Root.Style.IsVisible = searching;

        _favourites.Root.Style.IsVisible = !searching;
        _recents.Root.Style.IsVisible = !searching;
        if (!searching)
        {
            _favourites.Show(_client.Summaries.Where(s => s.Favourite), ready);
            _recents.Show(_client.Summaries.Where(s => s.Recent != null).OrderBy(s => s.Recent), ready);
        }
    }

    private AppSummary? Find(string id)
    {
        foreach (var s in _client.Summaries)
        {
            if (s.Id == id) return s;
        }

        return null;
    }

    private void Launch(AppSummary app)
    {
        Safe(() =>
        {
            var result = _client.Launch(app.Id);
            if (result.StartsWith("ok", StringComparison.Ordinal))
            {
                Close();
                return;
            }

            _flash = result;
            _flashUntil = Environment.TickCount64 + 6000;
        });
    }

    private static void Safe(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            Logger.Warning("[Umbra.XivDesktop] popup action failed: " + ex.Message);
        }
    }

    /// <summary>
    /// The pointer is over an open popup whose node we cannot reach (Umbra's MenuPopup): then any hovered ImGui
    /// window counts, which while that popup is open is the popup itself or the toolbar under it.
    /// </summary>
    internal static bool IsOver(WidgetPopup popup) => popup.IsOpen && ImGui.IsWindowHovered(ImGuiHoveredFlags.AnyWindow);

    internal static Node N(string cls, string? value = null)
    {
        var classes = new ObservableHashSet<string>();
        foreach (var c in cls.Split(' ', StringSplitOptions.RemoveEmptyEntries)) classes.Add(c);
        return new Node { ClassList = classes, NodeValue = value };
    }

    /// <summary>A titled list of pooled app buttons.</summary>
    private sealed class Section
    {
        private readonly ButtonNode[] _buttons;
        private readonly AppSummary?[] _apps;

        public Section(string title, int capacity, Action<AppSummary> launch)
        {
            Root = N("section");
            Root.AppendChild(N("section-title", title));
            var list = N("list");
            Root.AppendChild(list);
            _buttons = new ButtonNode[capacity];
            _apps = new AppSummary?[capacity];
            for (var i = 0; i < capacity; i++)
            {
                var index = i;
                var button = new ButtonNode($"{title}-{i}", "", null, isGhost: true, isSmall: true);
                button.OnClick += _ =>
                {
                    if (_apps[index] is { } app) launch(app);
                };
                button.OnRightClick += _ =>
                {
                    if (_apps[index] is { } app) Framework.Service<XivDesktopClient>().ToggleFavourite(app.Id);
                };
                _buttons[i] = button;
                list.AppendChild(button);
            }
        }

        public Node Root { get; }

        public void Show(IEnumerable<AppSummary> apps, bool ready)
        {
            var i = 0;
            foreach (var app in apps)
            {
                if (i >= _buttons.Length) break;
                _apps[i] = app;
                var b = _buttons[i];
                var label = (app.Favourite ? "★ " : "") + app.Name;
                if (b.Label != label) b.Label = label;
                b.Tooltip = (app.Generic.Length > 0 ? app.Generic + "\n" : "") + app.Id
                            + (app.Launchable ? "" : "\nTerminal app: not launched by XivDesktop v0")
                            + "\nRight-click: toggle favourite";
                b.IsDisabled = !ready || !app.Launchable;
                b.Style.IsVisible = true;
                i++;
            }

            if (i == 0) Root.Style.IsVisible = false;
            for (; i < _buttons.Length; i++)
            {
                _apps[i] = null;
                _buttons[i].Style.IsVisible = false;
            }
        }
    }
}
