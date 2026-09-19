using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using Lumina.Excel.Sheets;
using XivDesktop.Core.Ask;

namespace XivDesktop.Plugin.Ask;

/// <summary>
/// Builds the speaker catalog from the game sheets: every named ENpcResident with a drawable ENpcBase
/// (with its first known location from the Level sheet), the player's unlocked minions (Companion) and
/// mounts (Mount), the job pets (Pet) and "Myself". The sheet scan runs on the thread pool once per
/// session and is cached; only the unlock checks (game memory) run on the framework thread, and are
/// refreshed when the picker opens.
/// </summary>
public sealed class SpeakerSheets
{
    private readonly IDataManager data;
    private readonly IFramework framework;
    private readonly IPluginLog log;
    private readonly object gate = new();
    private List<SpeakerEntry>? npcs;
    private List<SpeakerEntry>? pets;
    private Task? scan;

    public SpeakerSheets(IDataManager data, IFramework framework, IPluginLog log)
    {
        this.data = data;
        this.framework = framework;
        this.log = log;
    }

    /// <summary>The current snapshot (empty until the first scan finished).</summary>
    public SpeakerCatalog Catalog { get; private set; } = SpeakerCatalog.Empty;

    public bool Scanning => scan is { IsCompleted: false };

    /// <summary>Raised on the framework thread when <see cref="Catalog"/> changed.</summary>
    public event Action? Changed;

    /// <summary>
    /// Refreshes unlocks now (framework thread) and starts the NPC scan if it has not run yet. Safe to call
    /// often: the sheet scan runs once.
    /// </summary>
    public void Refresh(string playerName)
    {
        var (minions, mounts) = Unlocked();
        lock (gate)
        {
            if (scan is null)
            {
                scan = Task.Run(() =>
                {
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    try
                    {
                        var n = ScanNpcs();
                        var p = ScanPets();
                        lock (gate)
                        {
                            npcs = n;
                            pets = p;
                        }

                        log.Information("XivDesktop ask: {Npcs} NPC speakers and {Pets} pets from the sheets in {Ms} ms", n.Count, p.Count, sw.ElapsedMilliseconds);
                    }
                    catch (Exception ex)
                    {
                        log.Warning(ex, "XivDesktop ask: scanning the NPC sheets failed");
                        lock (gate)
                        {
                            npcs = [];
                            pets = [];
                        }
                    }

                    _ = framework.RunOnFrameworkThread(() => Publish(playerName, minions, mounts));
                });
            }
        }

        Publish(playerName, minions, mounts);
    }

    private void Publish(string playerName, List<SpeakerEntry> minions, List<SpeakerEntry> mounts)
    {
        List<SpeakerEntry> all;
        lock (gate)
        {
            all = new List<SpeakerEntry>((npcs?.Count ?? 0) + minions.Count + mounts.Count + (pets?.Count ?? 0) + 1)
            {
                new(SpeakerSource.Self, 0, string.IsNullOrWhiteSpace(playerName) ? "You" : playerName, "yourself"),
            };
            all.AddRange(npcs ?? []);
            all.AddRange(minions);
            all.AddRange(mounts);
            all.AddRange(pets ?? []);
        }

        Catalog = new SpeakerCatalog(all);
        try
        {
            Changed?.Invoke();
        }
        catch (Exception ex)
        {
            log.Debug(ex, "XivDesktop ask: catalog Changed handler failed");
        }
    }

    /// <summary>Unlocked minions and mounts (framework thread: the checks read game state).</summary>
    private unsafe (List<SpeakerEntry> Minions, List<SpeakerEntry> Mounts) Unlocked()
    {
        var minions = new List<SpeakerEntry>();
        var mounts = new List<SpeakerEntry>();
        try
        {
            var ui = UIState.Instance();
            if (ui != null)
            {
                foreach (var c in data.GetExcelSheet<Companion>())
                {
                    var name = c.Singular.ExtractText();
                    if (name.Length == 0 || c.Model.RowId == 0 || !ui->IsCompanionUnlocked(c.RowId))
                        continue;
                    minions.Add(new SpeakerEntry(SpeakerSource.Minion, c.RowId, Capitalise(name), "minion", "", (int)c.Model.RowId));
                }
            }

            var ps = PlayerState.Instance();
            if (ps != null)
            {
                foreach (var m in data.GetExcelSheet<Mount>())
                {
                    var name = m.Singular.ExtractText();
                    if (name.Length == 0 || m.ModelChara.RowId == 0 || !ps->IsMountUnlocked(m.RowId))
                        continue;
                    mounts.Add(new SpeakerEntry(SpeakerSource.Mount, m.RowId, Capitalise(name), "mount", "", (int)m.ModelChara.RowId));
                }
            }
        }
        catch (Exception ex)
        {
            log.Warning(ex, "XivDesktop ask: reading minion/mount unlocks failed");
        }

        return (minions, mounts);
    }

    private List<SpeakerEntry> ScanNpcs()
    {
        // First location of each ENpc: Level rows of type 8 (ENpc) → territory name.
        var where = new Dictionary<uint, string>();
        var territories = data.GetExcelSheet<TerritoryType>();
        foreach (var lvl in data.GetExcelSheet<Level>())
        {
            if (lvl.Type != 8 || lvl.Object.RowId == 0 || where.ContainsKey(lvl.Object.RowId))
                continue;
            if (!territories.TryGetRow(lvl.Territory.RowId, out var t))
                continue;
            var place = t.PlaceName.ValueNullable?.Name.ExtractText() ?? "";
            if (place.Length > 0)
                where[lvl.Object.RowId] = place;
        }

        var bases = data.GetExcelSheet<ENpcBase>();
        var list = new List<SpeakerEntry>(40000);
        foreach (var r in data.GetExcelSheet<ENpcResident>())
        {
            var name = r.Singular.ExtractText();
            if (name.Length == 0 || !bases.TryGetRow(r.RowId, out var b))
                continue;
            // Something drawable: a model (moogle, monster) or a humanoid body.
            if (b.ModelChara.RowId == 0 && b.Race.RowId == 0)
                continue;
            list.Add(new SpeakerEntry(SpeakerSource.Npc, r.RowId, Capitalise(name), r.Title.ExtractText(), where.GetValueOrDefault(r.RowId, ""), (int)b.ModelChara.RowId));
        }

        return list;
    }

    private List<SpeakerEntry> ScanPets()
    {
        var list = new List<SpeakerEntry>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in data.GetExcelSheet<Pet>())
        {
            var name = p.Name.ExtractText();
            if (name.Length == 0 || !seen.Add(name))
                continue;
            list.Add(new SpeakerEntry(SpeakerSource.Pet, p.RowId, Capitalise(name), "summon"));
        }

        return list;
    }

    /// <summary>One entry read straight from its sheet row (before the scan finished); null when there is no such row.</summary>
    public SpeakerEntry? Lookup(SpeakerSource source, uint id)
    {
        try
        {
            switch (source)
            {
                case SpeakerSource.Npc when data.GetExcelSheet<ENpcResident>().TryGetRow(id, out var r) && r.Singular.ExtractText() is { Length: > 0 } n:
                    return new SpeakerEntry(source, id, Capitalise(n), r.Title.ExtractText(), "",
                        data.GetExcelSheet<ENpcBase>().TryGetRow(id, out var b) ? (int)b.ModelChara.RowId : 0);
                case SpeakerSource.Minion when data.GetExcelSheet<Companion>().TryGetRow(id, out var c) && c.Singular.ExtractText() is { Length: > 0 } n:
                    return new SpeakerEntry(source, id, Capitalise(n), "minion", "", (int)c.Model.RowId);
                case SpeakerSource.Mount when data.GetExcelSheet<Mount>().TryGetRow(id, out var m) && m.Singular.ExtractText() is { Length: > 0 } n:
                    return new SpeakerEntry(source, id, Capitalise(n), "mount", "", (int)m.ModelChara.RowId);
                case SpeakerSource.Pet when data.GetExcelSheet<Pet>().TryGetRow(id, out var p) && p.Name.ExtractText() is { Length: > 0 } n:
                    return new SpeakerEntry(source, id, Capitalise(n), "summon");
            }
        }
        catch (Exception ex)
        {
            log.Debug(ex, "XivDesktop ask: reading {Source} {Id} failed", source, id);
        }

        return null;
    }

    /// <summary>The look of an NPC from its ENpcBase row (NpcEquip gear when the row names one); null when unknown.</summary>
    public NpcLook? NpcLookFor(uint enpcId)
    {
        if (!data.GetExcelSheet<ENpcBase>().TryGetRow(enpcId, out var b))
            return null;
        if (b.ModelChara.RowId != 0 && b.Race.RowId == 0)
            return NpcLook.Model((int)b.ModelChara.RowId, b.Scale);

        ReadOnlySpan<byte> customize =
        [
            (byte)b.Race.RowId, b.Gender, b.BodyType, b.Height, (byte)b.Tribe.RowId, b.Face, b.HairStyle, b.HairHighlight,
            b.SkinColor, b.EyeHeterochromia, b.HairColor, b.HairHighlightColor, b.FacialFeature, b.FacialFeatureColor,
            b.Eyebrows, b.EyeColor, b.EyeShape, b.Nose, b.Jaw, b.Mouth, b.LipColor, b.BustOrTone1, b.ExtraFeature1,
            b.ExtraFeature2OrBust, b.FacePaint, b.FacePaintColor,
        ];

        if (b.NpcEquip.RowId != 0 && b.NpcEquip.ValueNullable is { } e)
        {
            return NpcLook.FromENpc((int)b.ModelChara.RowId, customize,
                [e.ModelHead, e.ModelBody, e.ModelHands, e.ModelLegs, e.ModelFeet, e.ModelEars, e.ModelNeck, e.ModelWrists, e.ModelLeftRing, e.ModelRightRing],
                [S(e.DyeHead), S(e.DyeBody), S(e.DyeHands), S(e.DyeLegs), S(e.DyeFeet), S(e.DyeEars), S(e.DyeNeck), S(e.DyeWrists), S(e.DyeLeftRing), S(e.DyeRightRing)],
                [S(e.Dye2Head), S(e.Dye2Body), S(e.Dye2Hands), S(e.Dye2Legs), S(e.Dye2Feet), S(e.Dye2Ears), S(e.Dye2Neck), S(e.Dye2Wrists), S(e.Dye2LeftRing), S(e.Dye2RightRing)],
                e.ModelMainHand, e.ModelOffHand, S(e.DyeMainHand), S(e.DyeOffHand), b.Scale);
        }

        return NpcLook.FromENpc((int)b.ModelChara.RowId, customize,
            [b.ModelHead, b.ModelBody, b.ModelHands, b.ModelLegs, b.ModelFeet, b.ModelEars, b.ModelNeck, b.ModelWrists, b.ModelLeftRing, b.ModelRightRing],
            [S(b.DyeHead), S(b.DyeBody), S(b.DyeHands), S(b.DyeLegs), S(b.DyeFeet), S(b.DyeEars), S(b.DyeNeck), S(b.DyeWrists), S(b.DyeLeftRing), S(b.DyeRightRing)],
            [S(b.Dye2Head), S(b.Dye2Body), S(b.Dye2Hands), S(b.Dye2Legs), S(b.Dye2Feet), S(b.Dye2Ears), S(b.Dye2Neck), S(b.Dye2Wrists), S(b.Dye2LeftRing), S(b.Dye2RightRing)],
            b.ModelMainHand, b.ModelOffHand, S(b.DyeMainHand), S(b.DyeOffHand), b.Scale);
    }

    private static byte S(Lumina.Excel.RowRef<Stain> s) => (byte)s.RowId;

    private static string Capitalise(string s) => s.Length > 0 && char.IsLower(s[0]) ? char.ToUpperInvariant(s[0]) + s[1..] : s;
}
