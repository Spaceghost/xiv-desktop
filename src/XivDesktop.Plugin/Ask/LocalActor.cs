using System.Numerics;
using System.Text;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using XivDesktop.Core.Ask;
using CopyFlags = FFXIVClientStructs.FFXIV.Client.Game.Character.CharacterSetupContainer.CopyFlags;

namespace XivDesktop.Plugin.Ask;

/// <summary>
/// A character that exists only in this client: allocated in the ClientObjectManager's local battle
/// character slots, dressed, placed and drawn, then deleted again. The server never hears of it. This is
/// the approach posing tools use (Brio's ActorSpawnService / ActorRedrawService, GPL-3.0, read for the
/// call sequence only): CreateBattleCharacter → GetObjectByIndex → CharacterSetup.CopyFromCharacter (or
/// ModelContainer/DrawData set directly) → wait for IsReadyToDraw → EnableDraw; DeleteObjectByIndex to
/// remove. Brio does this in GPose; outside GPose it is less trodden, so every step is checked and the
/// caller falls back to the dialogue without an actor if it fails. Framework thread only.
/// </summary>
public sealed unsafe class LocalActor : IDisposable
{
    private const uint Invalid = 0xFFFFFFFF;
    private ushort index;
    private Character* expected;
    private bool drawn;
    private float fade;
    private float drawWait;

    private LocalActor(ushort index, Character* chara)
    {
        this.index = index;
        expected = chara;
    }

    /// <summary>Still ours: the slot holds the object we created.</summary>
    public bool Alive
    {
        get
        {
            if (expected == null)
                return false;
            var com = ClientObjectManager.Instance();
            return com != null && (Character*)com->GetObjectByIndex(index) == expected;
        }
    }

    public Character* Chara => Alive ? expected : null;

    public bool Drawn => drawn;

    /// <summary>Creates the actor. Returns null with a reason when the game refused or something looked wrong.</summary>
    public static LocalActor? Spawn(NpcLook? look, Character* copyFrom, string name, Vector3 position, float yaw, out string error)
    {
        error = "";
        var com = ClientObjectManager.Instance();
        if (com == null)
        {
            error = "ClientObjectManager is not available";
            return null;
        }

        var idx = com->CreateBattleCharacter();
        if (idx == Invalid || idx > ushort.MaxValue)
        {
            error = "the game had no free local character slot";
            return null;
        }

        var chara = (Character*)com->GetObjectByIndex((ushort)idx);
        if (chara == null)
        {
            com->DeleteObjectByIndex((ushort)idx, 0);
            error = "the new local character could not be read back";
            return null;
        }

        var actor = new LocalActor((ushort)idx, chara);
        try
        {
            if (copyFrom != null)
            {
                // A copy of the player: race, customize, gear and weapons; no companion, mount or position.
                chara->CharacterSetup.CopyFromCharacter(copyFrom, CopyFlags.WeaponHiding);
            }
            else if (look is not null)
            {
                Dress(chara, look);
            }

            SetName(&chara->GameObject, name);
            chara->GameObject.TargetableStatus &= ~ObjectTargetableFlags.IsTargetable;
            chara->GameObject.DefaultPosition = position;
            chara->GameObject.DefaultRotation = yaw;
            chara->GameObject.SetPosition(position.X, position.Y, position.Z);
            chara->GameObject.SetRotation(yaw);
            chara->Alpha = 0f;
            return actor;
        }
        catch (Exception ex)
        {
            actor.Dispose();
            error = ex.Message;
            return null;
        }
    }

    private static void Dress(Character* chara, NpcLook look)
    {
        chara->ModelContainer.ModelCharaId = look.ModelCharaId;
        if (!look.IsHuman)
            return;
        var dst = chara->DrawData.CustomizeData.Data;
        for (var i = 0; i < NpcLook.CustomizeLength && i < dst.Length; i++)
            dst[i] = look.Customize[i];
        var eq = chara->DrawData.EquipmentModelIds;
        for (var i = 0; i < NpcLook.EquipmentSlots && i < eq.Length; i++)
        {
            var m = look.Equipment[i];
            eq[i] = new EquipmentModelId { Id = m.Id, Variant = m.Variant, Stain0 = m.Stain0, Stain1 = m.Stain1 };
        }

        if (!look.MainHand.IsEmpty)
            chara->DrawData.Weapon(DrawDataContainer.WeaponSlot.MainHand).ModelId = Weapon(look.MainHand);
        if (!look.OffHand.IsEmpty)
            chara->DrawData.Weapon(DrawDataContainer.WeaponSlot.OffHand).ModelId = Weapon(look.OffHand);
    }

    private static WeaponModelId Weapon(WeaponModel w) =>
        new() { Id = w.Id, Type = w.Type, Variant = w.Variant, Stain0 = w.Stain0, Stain1 = w.Stain1 };

    private static void SetName(GameObject* o, string name)
    {
        var span = o->Name;
        span.Clear();
        var bytes = Encoding.UTF8.GetBytes(name);
        var n = Math.Min(bytes.Length, span.Length - 1);
        bytes.AsSpan(0, n).CopyTo(span);
    }

    /// <summary>
    /// Per frame: turns drawing on once the model is ready (giving up after <paramref name="timeout"/>
    /// seconds) and fades in. Returns false when the actor is gone or never became drawable.
    /// </summary>
    public bool Tick(float dt, float fadeSeconds = 0.6f, float timeout = 5f)
    {
        var c = Chara;
        if (c == null)
            return false;
        if (!drawn)
        {
            drawWait += dt;
            if (c->GameObject.IsReadyToDraw())
            {
                c->GameObject.EnableDraw();
                drawn = true;
            }
            else if (drawWait > timeout)
            {
                return false;
            }

            return true;
        }

        if (fade < 1f)
        {
            fade = fadeSeconds <= 0 ? 1f : Math.Min(1f, fade + dt / fadeSeconds);
            c->Alpha = fade;
        }

        return true;
    }

    public void Place(Vector3 position, float yaw)
    {
        var c = Chara;
        if (c == null)
            return;
        c->GameObject.SetPosition(position.X, position.Y, position.Z);
        c->GameObject.SetRotation(yaw);
    }

    /// <summary>The looping base animation (talk, listen, walk, run, idle); 0 clears it.</summary>
    public void SetLoop(ushort timeline)
    {
        var c = Chara;
        if (c != null && c->Timeline.BaseOverride != timeline)
            c->Timeline.BaseOverride = timeline;
    }

    /// <summary>A one-shot gesture on top of the loop.</summary>
    public void Play(ushort timeline)
    {
        var c = Chara;
        if (c != null && timeline != 0)
            c->Timeline.TimelineSequencer.PlayTimeline(timeline);
    }

    public Vector3 Position => Chara is var c && c != null ? c->GameObject.Position : default;

    /// <summary>Drops the reference without deleting (the game already cleared its local characters).</summary>
    public void Forget() => expected = null;

    public void Dispose()
    {
        if (expected == null)
            return;
        var com = ClientObjectManager.Instance();
        if (com != null && (Character*)com->GetObjectByIndex(index) == expected)
        {
            try
            {
                if (drawn)
                    expected->GameObject.DisableDraw();
            }
            finally
            {
                com->DeleteObjectByIndex(index, 0);
            }
        }

        expected = null;
    }
}
