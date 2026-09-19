using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Colors;
using XivDesktop.Core;
using XivDesktop.Core.Ask;

namespace XivDesktop.Plugin.Ask;

/// <summary>The "Ask" tab of /desktop settings (ImGui, like the rest of the settings window).</summary>
public sealed class AskSettings
{
    private readonly AskModule module;
    private readonly Configuration config;
    private readonly Action save;
    private string search = "";
    private int tab;

    public AskSettings(AskModule module, Configuration config, Action save)
    {
        this.module = module;
        this.config = config;
        this.save = save;
    }

    public void Draw()
    {
        var changed = false;
        var service = module.Service;

        ImGui.TextUnformatted($"Speaker: {service.Speaker.Name} ({(config.AskSpeaker.Length > 0 ? config.AskSpeaker : SpeakerPresets.DefaultKey)})");
        ImGui.TextColored(ImGuiColors.DalamudGrey, $"Dialogue: {module.UiNote}. NPC: {(service.HasActor ? "spawned" : service.ActorNote.Length > 0 ? service.ActorNote : "not summoned")}.");
        foreach (var (preset, key, _) in SpeakerPresets.All)
        {
            if (ImGui.SmallButton(preset))
                service.SetSpeaker(key);
            ImGui.SameLine();
        }

        if (ImGui.SmallButton("Open the in-game picker…"))
            module.OpenPicker();

        ImGui.Separator();
        var spawn = config.AskSpawnNpc;
        if (ImGui.Checkbox("Summon a character to talk to (NPC on)", ref spawn))
        {
            config.AskSpawnNpc = spawn;
            changed = true;
        }

        var follow = config.AskFollow;
        if (ImGui.Checkbox("It follows me like a pet", ref follow))
        {
            config.AskFollow = follow;
            changed = true;
        }

        var emotes = config.AskEmotes;
        if (ImGui.Checkbox("Emote cues (from the answer, else from what I typed)", ref emotes))
        {
            config.AskEmotes = emotes;
            changed = true;
        }

        var gestures = config.AskPlayerGestures;
        if (ImGui.Checkbox("My character turns and gestures when I ask", ref gestures))
        {
            config.AskPlayerGestures = gestures;
            changed = true;
        }

        var route = config.AskRouteSlashAsk;
        if (ImGui.Checkbox("Route /ask to the NPC as well", ref route))
        {
            config.AskRouteSlashAsk = route;
            changed = true;
        }

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Takes effect on the next plugin load, and only if no other plugin already owns /ask\n(ghostty-dalamud's chat panel does). \"/npc\" always works.");
        if (config.AskRouteSlashAsk && !module.OwnsSlashAsk)
            ImGui.TextColored(ImGuiColors.DalamudGrey, "/ask is owned by another plugin; use /npc.");

        var speed = config.AskReadingSpeed;
        ImGui.SetNextItemWidth(200);
        if (ImGui.SliderInt("Reading speed", ref speed, 1, 5, speed switch { 1 => "slow", 2 => "relaxed", 3 => "normal", 4 => "fast", _ => "instant" }))
        {
            config.AskReadingSpeed = speed;
            changed = true;
        }

        var selfTitle = config.AskSelfTitle;
        ImGui.SetNextItemWidth(200);
        if (ImGui.InputText("\"Myself\" title", ref selfTitle, 40))
        {
            config.AskSelfTitle = selfTitle;
            changed = true;
        }

        var backend = (int)config.AskBackend;
        ImGui.SetNextItemWidth(260);
        if (ImGui.Combo("Backend", ref backend, "almanac gateway (in-world dialogue)\0ghostty terminal (/term ask)\0"))
        {
            config.AskBackend = (AskBackendKind)backend;
            changed = true;
        }

        if (config.AskBackend == AskBackendKind.Gateway)
        {
            var gateway = config.AskGateway;
            ImGui.SetNextItemWidth(260);
            if (ImGui.InputTextWithHint("Gateway", AlmanacPaths.DefaultGateway, ref gateway, 200))
            {
                config.AskGateway = gateway.Trim();
                changed = true;
            }

            var model = config.AskModel;
            ImGui.SetNextItemWidth(260);
            if (ImGui.InputText("Model", ref model, 80))
            {
                config.AskModel = model.Trim();
                changed = true;
            }
        }

        if (changed)
            save();

        ImGui.Separator();
        Picker(service);
    }

    private void Picker(AskService service)
    {
        var catalog = module.Sheets.Catalog;
        if (catalog.Entries.Count == 0 && !module.Sheets.Scanning && ImGui.Button("Load the speaker list"))
            module.Sheets.Refresh("");
        if (module.Sheets.Scanning)
            ImGui.TextColored(ImGuiColors.DalamudGrey, "reading the game's NPC list…");

        string[] tabs = ["NPCs", "Minions", "Mounts", "Summons", "Myself", "★"];
        for (var i = 0; i < tabs.Length; i++)
        {
            if (i > 0)
                ImGui.SameLine();
            if (ImGui.RadioButton(tabs[i], tab == i))
                tab = i;
        }

        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("##asksearch", "Search by name or title…", ref search, 80);
        SpeakerSource? source = tab switch { 0 => SpeakerSource.Npc, 1 => SpeakerSource.Minion, 2 => SpeakerSource.Mount, 3 => SpeakerSource.Pet, 4 => SpeakerSource.Self, _ => null };
        var results = catalog.Search(search, source, config.AskFavourites, config.AskRecents, 80);
        if (tab == 5)
            results = results.Where(e => UserLists.Contains(config.AskFavourites, e.Key)).ToList();

        if (!ImGui.BeginChild("##askresults", new System.Numerics.Vector2(-1, 220), true))
        {
            ImGui.EndChild();
            return;
        }

        foreach (var e in results)
        {
            ImGui.PushID(e.Key);
            var fav = UserLists.Contains(config.AskFavourites, e.Key);
            if (ImGui.SmallButton(fav ? "★" : "☆"))
                service.ToggleFavourite(e);
            ImGui.SameLine();
            var label = e.Title.Length > 0 ? $"{e.Name} — {e.Title}" : e.Name;
            if (ImGui.Selectable(label, service.Speaker.Key == e.Key))
                service.SetSpeaker(e.Key);
            if (e.Location.Length > 0 && ImGui.IsItemHovered())
                ImGui.SetTooltip(e.Location);
            ImGui.PopID();
        }

        ImGui.EndChild();
    }
}
