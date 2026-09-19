using System.Numerics;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using XivDesktop.Core.Ask;
using XivDesktop.Core.Claude;
using XivDesktop.Plugin.Ask;
using AskLook = XivDesktop.Core.Ask.NpcLook;
using ClaudeLook = XivDesktop.Core.Claude.NpcLook;

namespace XivDesktop.Plugin.Claude;

/// <summary>
/// The Claude NPCs: one client-side character per session, standing beside the player. It is the same
/// machinery the <c>/npc</c> assistant uses — <see cref="LocalActor"/> for the spawn, <see cref="Follower"/>
/// for the follow behaviour and <see cref="EmoteTable"/>/<see cref="Timelines"/> for the animations —
/// with one actor per session instead of one, and the session's own deterministic look.
/// </summary>
/// <remarks>
/// Nothing reaches the server: the character is allocated in the client's local slots, dressed, placed and
/// deleted again, exactly as <c>Ask/LocalActor.cs</c> documents. Framework thread only. Nothing here has
/// been observed in the game.
/// </remarks>
public sealed unsafe class ClaudeAvatars : INpcAvatar, IDisposable
{
    /// <summary>The player has to be standing still for a Claude to appear beside them.</summary>
    private readonly Dictionary<string, Avatar> avatars = new(StringComparer.OrdinalIgnoreCase);
    private readonly IObjectTable objects;
    private readonly IFramework framework;
    private readonly IPluginLog log;
    private readonly Func<bool> enabled;

    public ClaudeAvatars(IFramework framework, IObjectTable objects, IPluginLog log, Func<bool> enabled)
    {
        this.framework = framework;
        this.objects = objects;
        this.log = log;
        this.enabled = enabled;
        framework.Update += OnUpdate;
    }

    public bool Available => true;

    /// <summary>Why the last summon failed, for the panel ("" when it did not).</summary>
    public string Note { get; private set; } = "";

    public bool Summon(string sessionKey, ClaudeLook look)
    {
        if (!enabled())
            return false;
        Dismiss(sessionKey);
        var player = objects.LocalPlayer;
        if (player is null)
        {
            Note = "you are not in the world yet";
            return false;
        }

        try
        {
            var follower = new Follower();
            var p = player.Position;
            // Each Claude stands a little further round than the last, so several do not overlap.
            follower.Spawn(p.X, p.Y, p.Z, player.Rotation, 2.0 + (avatars.Count * 0.7));
            var position = new Vector3((float)follower.X, p.Y, (float)follower.Z);
            var actor = LocalActor.Spawn(Dress(look), null, look.Nameplate, position, (float)follower.Yaw, out var error);
            if (actor is null)
            {
                Note = error;
                log.Warning("XivDesktop claude: summoning {Session} failed ({Error})", sessionKey, error);
                return false;
            }

            Note = "";
            avatars[sessionKey] = new Avatar(actor, follower);
            return true;
        }
        catch (Exception ex)
        {
            Note = ex.GetType().Name;
            log.Warning(ex, "XivDesktop claude: summoning {Session} threw", sessionKey);
            return false;
        }
    }

    public void Play(string sessionKey, NpcCue cue)
    {
        if (avatars.TryGetValue(sessionKey, out var avatar))
            avatar.Cue = cue;
    }

    public void Dismiss(string sessionKey)
    {
        if (!avatars.Remove(sessionKey, out var avatar))
            return;
        try
        {
            avatar.Actor.Dispose();
        }
        catch (Exception ex)
        {
            log.Debug(ex, "XivDesktop claude: dismissing {Session} threw", sessionKey);
        }
    }

    public void Dispose()
    {
        framework.Update -= OnUpdate;
        foreach (var key in avatars.Keys.ToList())
            Dismiss(key);
    }

    /// <summary>
    /// The session's look as the spawner wants it: a human body (ModelChara 0) built from the customise
    /// bytes, the five visible equipment slots and the main hand.
    /// </summary>
    private static AskLook Dress(ClaudeLook look)
    {
        var customize = new byte[AskLook.CustomizeLength];
        look.Customize.AsSpan(0, Math.Min(look.Customize.Length, customize.Length)).CopyTo(customize);
        var equipment = new EquipModel[AskLook.EquipmentSlots];
        for (var slot = 0; slot < look.Equipment.Count && slot < equipment.Length; slot++)
            equipment[slot] = new EquipModel(look.Equipment[slot], 1);
        return new AskLook(0, customize, equipment, new WeaponModel(look.Weapon, 0, 1), default);
    }

    private void OnUpdate(IFramework f)
    {
        if (avatars.Count == 0)
            return;
        var dt = (float)f.UpdateDelta.TotalSeconds;
        var player = objects.LocalPlayer;
        foreach (var (key, avatar) in avatars.ToList())
        {
            try
            {
                if (!avatar.Actor.Tick(dt))
                {
                    log.Information("XivDesktop claude: {Session}'s NPC is gone", key);
                    Dismiss(key);
                    continue;
                }

                if (player is null)
                    continue;
                var p = player.Position;
                var camera = CameraPosition();
                // "Asking" holds it still and facing the player: that is the looking-up moment.
                var talking = avatar.Cue.Pose is NpcPose.LookUp or NpcPose.Typing or NpcPose.Reading;
                avatar.Follower.Update(dt, p.X, p.Y, p.Z, player.Rotation, talking, camera.X, camera.Z);
                avatar.Actor.Place(
                    new Vector3((float)avatar.Follower.X, (float)avatar.Follower.Y, (float)avatar.Follower.Z),
                    (float)avatar.Follower.Yaw);

                avatar.Actor.SetLoop(avatar.Follower.Gait switch
                {
                    Gait.Run => Timelines.Run,
                    Gait.Walk => Timelines.Walk,
                    _ => Loop(avatar.Cue.Pose),
                });

                // A pose change plays its emote once, and never while the NPC is moving.
                if (avatar.Cue.OneShot && avatar.Cue != avatar.Played)
                {
                    avatar.Played = avatar.Cue;
                    if (avatar.Follower.Gait == Gait.Idle && EmoteTable.Find(avatar.Cue.Emote.TrimStart('/')) is { } emote)
                        avatar.Actor.Play(emote.TimelineId);
                }
            }
            catch (Exception ex)
            {
                log.Warning(ex, "XivDesktop claude: {Session}'s NPC failed to update", key);
                Dismiss(key);
            }
        }
    }

    /// <summary>The standing loop for a pose: talking while it works, listening while it waits.</summary>
    private static ushort Loop(NpcPose pose) => pose switch
    {
        NpcPose.Typing or NpcPose.Reading => Timelines.StandTalk,
        NpcPose.LookUp => Timelines.StandListen,
        _ => Timelines.Idle,
    };

    private static Vector3 CameraPosition()
    {
        var cm = CameraManager.Instance();
        if (cm == null || cm->Camera == null)
            return new Vector3(float.NaN, float.NaN, float.NaN);
        return cm->Camera->CameraBase.SceneCamera.Object.Position;
    }

    private sealed class Avatar(LocalActor actor, Follower follower)
    {
        public LocalActor Actor { get; } = actor;

        public Follower Follower { get; } = follower;

        public NpcCue Cue { get; set; }

        /// <summary>The last one-shot cue actually played, so it is not replayed every frame.</summary>
        public NpcCue Played { get; set; }
    }
}
