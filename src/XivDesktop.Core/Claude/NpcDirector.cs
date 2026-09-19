namespace XivDesktop.Core.Claude;

/// <summary>What the Claude NPC should be doing right now.</summary>
public enum NpcPose
{
    /// <summary>Standing by.</summary>
    Idle,

    /// <summary>Working: the typing loop.</summary>
    Typing,

    /// <summary>Reading or searching: a slower, thinking loop.</summary>
    Reading,

    /// <summary>Needs a decision: looks up at the player and waits.</summary>
    LookUp,

    /// <summary>The turn finished.</summary>
    Nod,

    /// <summary>Something went wrong.</summary>
    Shrug,
}

/// <summary>One thing for the avatar to do: a pose, and optionally an emote to play once.</summary>
public readonly record struct NpcCue(NpcPose Pose, string Emote = "", string Line = "")
{
    /// <summary>True when the cue should be played as a one-off rather than held.</summary>
    public bool OneShot => Emote.Length > 0;
}

/// <summary>
/// Turns a session's state into what its NPC does. Pure and frame-driven: the plugin calls
/// <see cref="Cue"/> each time the session changes and plays whatever comes back, so the behaviour is
/// testable without the game.
/// </summary>
public sealed class NpcDirector
{
    private NpcPose last = NpcPose.Idle;

    /// <summary>The pose for <paramref name="activity"/>, and the emote when it just changed.</summary>
    public NpcCue Cue(SessionActivity activity, ToolKind? runningTool = null)
    {
        var pose = activity switch
        {
            SessionActivity.Asking => NpcPose.LookUp,
            SessionActivity.Broken => NpcPose.Shrug,
            SessionActivity.Starting => NpcPose.Reading,
            SessionActivity.Working => runningTool switch
            {
                ToolKind.Read or ToolKind.Search or ToolKind.Web => NpcPose.Reading,
                _ => NpcPose.Typing,
            },
            SessionActivity.Waiting => NpcPose.Nod,
            _ => NpcPose.Idle,
        };

        var changed = pose != last;
        last = pose;
        if (!changed)
            return new NpcCue(pose);

        return pose switch
        {
            // The emotes are the game's own command names; the plugin plays them if it can.
            NpcPose.LookUp => new NpcCue(pose, "/lookout", "needs a decision"),
            NpcPose.Nod => new NpcCue(pose, "/nod"),
            NpcPose.Shrug => new NpcCue(pose, "/shrug"),
            NpcPose.Reading => new NpcCue(pose, "/think"),
            NpcPose.Typing => new NpcCue(pose, "/psych"),
            _ => new NpcCue(pose),
        };
    }

    public void Reset() => last = NpcPose.Idle;
}

/// <summary>
/// What a spawned Claude avatar needs from whatever can actually put an object in the world. The plugin's
/// implementation reuses the <c>/npc</c> assistant's spawner and follow behaviour; the seam keeps Core
/// free of game types and lets the panel work with a real avatar or with none at all.
/// </summary>
public interface INpcAvatar
{
    /// <summary>False when no spawner is present; the panel then says the NPC is unavailable.</summary>
    bool Available { get; }

    /// <summary>Why the last summon did not work, for the panel ("" when it did).</summary>
    string Note { get; }

    /// <summary>Summons (or re-dresses) the avatar for a session. Returns false when it could not.</summary>
    bool Summon(string sessionKey, NpcLook look);

    /// <summary>Plays a cue on a summoned avatar.</summary>
    void Play(string sessionKey, NpcCue cue);

    /// <summary>Removes the avatar; called when the conversation is dismissed.</summary>
    void Dismiss(string sessionKey);
}

/// <summary>The seam with nothing behind it: every call is a no-op and <see cref="Available"/> is false.</summary>
public sealed class NoNpcAvatar : INpcAvatar
{
    public static readonly NoNpcAvatar Instance = new();

    public bool Available => false;

    public string Note => "nothing here can put an avatar in the world";

    public bool Summon(string sessionKey, NpcLook look) => false;

    public void Play(string sessionKey, NpcCue cue)
    {
    }

    public void Dismiss(string sessionKey)
    {
    }
}
