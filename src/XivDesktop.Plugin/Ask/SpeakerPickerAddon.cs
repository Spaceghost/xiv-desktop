using System.Numerics;
using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiToolKit.BaseTypes;
using KamiToolKit.Nodes;
using Lumina.Text.ReadOnly;
using XivDesktop.Core.Ask;

namespace XivDesktop.Plugin.Ask;

/// <summary>
/// "Who to ask": a native window (KamiToolKit) with a tab per source (NPCs, minions, mounts, summons,
/// yourself, favourites), a search box, a page of results, the selection's name, title and location,
/// and buttons to star it, spawn a live preview beside you, or make it the speaker.
/// </summary>
public sealed unsafe class SpeakerPickerAddon : NativeAddon
{
    private const int Rows = 14;
    private const float RowHeight = 24f;

    private readonly List<ListButtonNode> rows = [];
    private List<SpeakerEntry> results = [];
    private SearchInputNode? search;
    private TextNode? detail;
    private TextNode? status;
    private TextButtonNode? favButton;
    private SpeakerSource? source = SpeakerSource.Npc;
    private bool favouritesOnly;
    private string query = "";
    private int page;
    private SpeakerEntry? selected;

    public Func<SpeakerCatalog> Catalog { get; set; } = () => SpeakerCatalog.Empty;

    public Func<IReadOnlyList<string>> Favourites { get; set; } = () => [];

    public Func<IReadOnlyList<string>> Recents { get; set; } = () => [];

    public Func<string> Current { get; set; } = () => "";

    public Action<SpeakerEntry>? Chosen { get; set; }

    public Action<SpeakerEntry>? ToggleFavourite { get; set; }

    public Action<SpeakerEntry>? Preview { get; set; }

    public Action? Closed { get; set; }

    public static SpeakerPickerAddon Create() => new()
    {
        InternalName = "XivDesktopAskPicker",
        Title = new ReadOnlySeString("Who to ask".AsSpan()),
        Size = new Vector2(560, 600),
    };

    protected override void OnSetup(AtkUnitBase* addon, Span<AtkValue> atkValueSpan)
    {
        var x = ContentStartPosition.X;
        var y = ContentStartPosition.Y;
        var w = ContentSize.X;

        var tabs = new TabBarNode { Position = new Vector2(x, y), Size = new Vector2(w, 26), IsVisible = true };
        AddNode(tabs);
        tabs.AddTab(Se("NPCs"), () => SetSource(SpeakerSource.Npc, false));
        tabs.AddTab(Se("Minions"), () => SetSource(SpeakerSource.Minion, false));
        tabs.AddTab(Se("Mounts"), () => SetSource(SpeakerSource.Mount, false));
        tabs.AddTab(Se("Summons"), () => SetSource(SpeakerSource.Pet, false));
        tabs.AddTab(Se("Myself"), () => SetSource(SpeakerSource.Self, false));
        tabs.AddTab(Se("★"), () => SetSource(null, true), Se("Favourites"));
        y += 32;

        search = new SearchInputNode
        {
            Position = new Vector2(x, y),
            Size = new Vector2(w, 28),
            PlaceholderString = "Search by name or title…",
            IsVisible = true,
            OnInputReceived = s =>
            {
                query = s.ExtractText();
                page = 0;
                Refresh();
            },
        };
        AddNode(search);
        y += 34;

        for (var i = 0; i < Rows; i++)
        {
            var row = i;
            var node = new ListButtonNode
            {
                Position = new Vector2(x, y + i * RowHeight),
                Size = new Vector2(w, RowHeight - 2),
                IsVisible = false,
                OnClick = () => Select(row),
            };
            rows.Add(node);
            AddNode(node);
        }

        y += Rows * RowHeight + 6;
        var prev = new TextButtonNode { Position = new Vector2(x, y), Size = new Vector2(40, 26), String = Se("‹"), IsVisible = true, OnClick = () => Page(-1) };
        var next = new TextButtonNode { Position = new Vector2(x + 44, y), Size = new Vector2(40, 26), String = Se("›"), IsVisible = true, OnClick = () => Page(1) };
        status = new TextNode { Position = new Vector2(x + 92, y + 4), Size = new Vector2(w - 92, 20), FontSize = 12, TextColor = new Vector4(0.8f, 0.8f, 0.8f, 1), IsVisible = true };
        AddNode(prev);
        AddNode(next);
        AddNode(status);
        y += 32;

        detail = new TextNode
        {
            Position = new Vector2(x, y),
            Size = new Vector2(w, 44),
            FontSize = 14,
            TextFlags = TextFlags.WordWrap | TextFlags.MultiLine,
            IsVisible = true,
        };
        AddNode(detail);
        y += 50;

        favButton = new TextButtonNode { Position = new Vector2(x, y), Size = new Vector2(150, 28), String = Se("☆ Favourite"), IsVisible = true, OnClick = () => Act(ToggleFavourite, refresh: true) };
        var preview = new TextButtonNode { Position = new Vector2(x + 156, y), Size = new Vector2(150, 28), String = Se("Preview"), IsVisible = true, OnClick = () => Act(Preview, refresh: false) };
        var choose = new TextButtonNode { Position = new Vector2(x + w - 150, y), Size = new Vector2(150, 28), String = Se("Ask this one"), IsVisible = true, OnClick = () => Act(Chosen, refresh: true) };
        AddNode(favButton);
        AddNode(preview);
        AddNode(choose);

        selected = Catalog().Find(Current());
        Refresh();
    }

    protected override void OnFinalize(AtkUnitBase* addon)
    {
        rows.Clear();
        search = null;
        detail = status = null;
        favButton = null;
        Closed?.Invoke();
    }

    private void SetSource(SpeakerSource? s, bool favourites)
    {
        source = s;
        favouritesOnly = favourites;
        page = 0;
        Refresh();
    }

    private void Page(int delta)
    {
        page = Math.Max(0, page + delta);
        Refresh();
    }

    private void Select(int row)
    {
        var i = page * Rows + row;
        if (i < results.Count)
            selected = results[i];
        Refresh();
    }

    private void Act(Action<SpeakerEntry>? action, bool refresh)
    {
        if (selected is null)
            return;
        action?.Invoke(selected);
        if (refresh)
            Refresh();
    }

    /// <summary>Puts <paramref name="text"/> in the search box (empty only refreshes).</summary>
    public void SetSearch(string text)
    {
        if (text.Length > 0)
        {
            query = text;
            page = 0;
            if (search is not null)
                search.String = new ReadOnlySeString(text.AsSpan());
        }

        Refresh();
    }

    /// <summary>Re-runs the search (also called when the catalog finished scanning).</summary>
    public void Refresh()
    {
        if (!IsOpen || rows.Count == 0)
            return;
        var catalog = Catalog();
        var favs = Favourites();
        var found = catalog.Search(query, favouritesOnly ? null : source, favs, Recents(), limit: 300);
        if (favouritesOnly)
        {
            var set = new HashSet<string>(favs, StringComparer.OrdinalIgnoreCase);
            found = found.Where(e => set.Contains(e.Key)).ToList();
        }

        results = found;
        var pages = Math.Max(1, (results.Count + Rows - 1) / Rows);
        page = Math.Min(page, pages - 1);
        var favSet = new HashSet<string>(favs, StringComparer.OrdinalIgnoreCase);
        for (var r = 0; r < Rows; r++)
        {
            var i = page * Rows + r;
            var node = rows[r];
            if (i >= results.Count)
            {
                node.IsVisible = false;
                continue;
            }

            var e = results[i];
            var star = favSet.Contains(e.Key) ? "★ " : "";
            var extra = e.Title.Length > 0 ? $" — {e.Title}" : "";
            node.String = Se(star + e.Name + extra);
            node.Selected = selected is not null && selected.Key == e.Key;
            node.IsVisible = true;
        }

        if (status is not null)
        {
            var counting = catalog.Entries.Count <= 1 ? "reading the game's NPC list…" : $"{results.Count} found";
            status.String = Se($"{counting} · page {page + 1}/{pages}");
        }

        if (detail is not null)
        {
            detail.String = selected is null
                ? Se("Pick someone to ask.")
                : Se($"{selected.Name}{(selected.Title.Length > 0 ? $", {selected.Title}" : "")}\n{(selected.Location.Length > 0 ? selected.Location : SourceText(selected.Source))}{(selected.Key == Current() ? "  (current)" : "")}");
        }

        if (favButton is not null && selected is not null)
            favButton.String = Se(favSet.Contains(selected.Key) ? "★ Unfavourite" : "☆ Favourite");
    }

    private static string SourceText(SpeakerSource s) => s switch
    {
        SpeakerSource.Minion => "one of your minions",
        SpeakerSource.Mount => "one of your mounts",
        SpeakerSource.Pet => "a job summon (summon it once so its model can be learned)",
        SpeakerSource.Self => "a copy of your own character, advising you",
        _ => "location unknown",
    };

    private static ReadOnlySeString Se(string s) => new(s.Replace('\u0002', ' ').Replace('\u0003', ' ').Replace("\n", " · ", StringComparison.Ordinal).AsSpan());
}
