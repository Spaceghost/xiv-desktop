using System.Numerics;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using XivDesktop.Core;
using XivDesktop.Core.Ask;
using NativeCharacter = FFXIVClientStructs.FFXIV.Client.Game.Character.Character;
using NativeObject = FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject;

namespace XivDesktop.Plugin.Ask;

/// <summary>
/// "Ask an NPC": the conversation (client-side transcript, streamed from almanac's gateway), the summoned
/// local speaker (spawn, follow, animation, cues), the player's own gestures, and the dialogue view.
/// Everything that touches the game or the UI runs on the framework thread; the network runs on the
/// thread pool and is drained here each frame. Nothing is sent to the game server.
/// </summary>
public sealed unsafe class AskService : IDisposable
{
    private readonly Configuration config;
    private readonly Action save;
    private readonly IFramework framework;
    private readonly IClientState clientState;
    private readonly ICondition condition;
    private readonly IObjectTable objects;
    private readonly IPluginLog log;
    private readonly AskBackend backend;
    private readonly Func<bool> terminalAvailable;
    private readonly Action<string> postTerminal;
    private readonly Transcript transcript = new();
    private readonly CueFilter cues = new();
    private readonly TextReveal reveal = new();
    private readonly Follower follower = new();
    private readonly Queue<Cue> pendingCues = new();

    private IAskView? view;
    private LocalActor? actor;
    private LocalActor? preview;
    private float previewTime;
    private SpeakerEntry speaker = new(SpeakerSource.Npc, 0, "Studium researcher");
    private bool humanoid;
    private bool summoned;
    private bool answerHadCue;
    private string lastQuestion = "";
    private float turnPlayerFor;

    public AskService(Configuration config, Action save, IFramework framework, IClientState clientState, ICondition condition,
        IObjectTable objects, IPluginLog log, AskBackend backend, SpeakerSheets sheets, Func<bool> terminalAvailable, Action<string> postTerminal)
    {
        this.config = config;
        this.save = save;
        this.framework = framework;
        this.clientState = clientState;
        this.condition = condition;
        this.objects = objects;
        this.log = log;
        this.backend = backend;
        Sheets = sheets;
        this.terminalAvailable = terminalAvailable;
        this.postTerminal = postTerminal;
        framework.Update += OnUpdate;
        clientState.TerritoryChanged += OnTerritoryChanged;
        clientState.Logout += OnLogout;
    }

    public SpeakerSheets Sheets { get; }

    public SpeakerEntry Speaker => speaker;

    public bool Summoned => summoned;

    public bool HasActor => actor?.Alive == true;

    /// <summary>Why the last spawn did not produce a character ("" when it did or none was tried).</summary>
    public string ActorNote { get; private set; } = "";

    /// <summary>Attached by the plugin once the UI is ready (KamiToolKit, or the ImGui fallback).</summary>
    public void Attach(IAskView v)
    {
        view = v;
        v.Submitted = OnSubmitted;
        v.Dismissed = () => Dismiss("closed");
    }

    // Commands ---------------------------------------------------------------------------------

    /// <summary>
    /// "/ask" handling: "" summons for a conversation, "bye" dismisses, "as &lt;name or key&gt; [question]"
    /// switches speaker, anything else is a question. Returns a status line ("ok: …" / "error: …").
    /// </summary>
    public string Command(string args)
    {
        args = (args ?? "").Trim();
        var lower = args.ToLowerInvariant();
        if (lower is "bye" or "dismiss" or "goodbye" or "close")
        {
            Dismiss("bye");
            return "ok: dismissed";
        }

        if (lower is "who" or "pick" or "choose" or "npc")
            return "ok: picker";

        if (lower.StartsWith("as ", StringComparison.Ordinal))
        {
            var rest = args[3..].Trim();
            var (key, question) = SplitSpeaker(rest);
            if (key is null)
                return "ok: picker " + rest; // not a preset, key or favourite: let them search for it
            var r = SetSpeaker(key);
            if (r.StartsWith("error", StringComparison.Ordinal))
                return r;
            return question.Length > 0 ? Ask(question) : Summon();
        }

        return args.Length == 0 ? Summon() : Ask(args);
    }

    /// <summary>"scholar how do…" / "npc:123 hello" / "Alphinaud hello" (a favourite by name) → (key, rest).</summary>
    private (string? Key, string Question) SplitSpeaker(string text)
    {
        var space = text.IndexOf(' ');
        var first = space < 0 ? text : text[..space];
        var rest = space < 0 ? "" : text[(space + 1)..].Trim();
        if (SpeakerPresets.Resolve(first) is { } k)
            return (k, rest);
        // A favourite or recent by name, longest name first ("Y'shtola hello").
        foreach (var key in config.AskFavourites.Concat(config.AskRecents))
        {
            if (Sheets.Catalog.Find(key) is { } e && text.StartsWith(e.Name, StringComparison.OrdinalIgnoreCase))
                return (key, text[e.Name.Length..].Trim());
        }

        return (null, text);
    }

    public string SetSpeaker(string key)
    {
        var parsed = SpeakerEntry.ParseKey(key);
        if (parsed is null)
            return $"error: \"{key}\" is not a speaker key";
        var entry = Sheets.Catalog.Find(key) ?? Fallback(parsed.Value.Source, parsed.Value.Id);
        config.AskSpeaker = entry.Key;
        config.AskRecents = UserLists.PushRecent(config.AskRecents, entry.Key, 12);
        save();
        var wasSummoned = summoned;
        speaker = entry;
        if (wasSummoned)
        {
            // A new speaker is a new conversation.
            Despawn();
            transcript.Clear();
            summoned = false;
            Summon();
        }

        return $"ok: speaking with {entry.Name}";
    }

    private SpeakerEntry Fallback(SpeakerSource source, uint id) => source == SpeakerSource.Self
        ? new SpeakerEntry(SpeakerSource.Self, 0, PlayerName())
        : Sheets.Lookup(source, id) ?? new SpeakerEntry(source, id, $"{source} {id}");

    public string ToggleFavourite(SpeakerEntry e)
    {
        config.AskFavourites = UserLists.ToggleFavourite(config.AskFavourites, e.Key);
        save();
        return UserLists.Contains(config.AskFavourites, e.Key) ? $"ok: {e.Name} is a favourite" : $"ok: {e.Name} is no longer a favourite";
    }

    public string Summon()
    {
        if (config.AskBackend == AskBackendKind.Terminal)
            return "ok: the terminal backend has no dialogue; use /ask <question>";
        if (view is null)
            return "error: the dialogue is not ready yet";
        if (Sheets.Catalog.Entries.Count == 0 && !Sheets.Scanning)
            Sheets.Refresh(PlayerName());
        LoadSpeaker();
        if (!summoned)
        {
            summoned = true;
            reveal.Reset();
            TrySpawn();
        }

        view.SetSpeaker(DisplayName(assistant: true));
        view.Open();
        Render();
        return $"ok: {speaker.Name} is listening";
    }

    public string Ask(string question)
    {
        question = question.Trim();
        if (question.Length == 0)
            return Summon();
        if (config.AskBackend == AskBackendKind.Terminal)
        {
            if (!terminalAvailable())
                return "error: the terminal backend needs ghostty-dalamud";
            postTerminal("ask " + question);
            return "ok: asked in a ghostty terminal";
        }

        var s = Summon();
        if (s.StartsWith("error", StringComparison.Ordinal))
            return s;
        if (transcript.Pending)
        {
            backend.Cancel();
            transcript.Fail("(interrupted)");
        }

        transcript.Ask(question);
        lastQuestion = question;
        cues.Flush();
        pendingCues.Clear();
        answerHadCue = false;
        reveal.Reset();
        reveal.CharsPerSecond = TextReveal.RateFor(config.AskReadingSpeed);
        var persona = new Persona(speaker.Name, speaker.Title, speaker.Kind, PlayerName());
        backend.Start(string.IsNullOrWhiteSpace(config.AskGateway) ? AlmanacPaths.DefaultGateway : config.AskGateway,
            transcript.BuildRequest(persona, config.AskModel));
        PlayerAsks();
        return $"ok: asked {speaker.Name}";
    }

    public void Dismiss(string reason)
    {
        backend.Cancel();
        if (transcript.Pending)
            transcript.Fail("(dismissed)");
        Despawn();
        summoned = false;
        transcript.Clear();
        view?.Close();
        if (reason is not ("closed" or "bye"))
            log.Information("XivDesktop ask: dismissed ({Reason})", reason);
    }

    private void OnSubmitted(string text)
    {
        if (text.Length == 0)
        {
            // Enter on an empty line: finish the typewriter, like clicking through Talk.
            if (transcript.Turns.Count > 0)
                reveal.Skip(transcript.Turns[^1].Text);
            return;
        }

        if (text.Equals("bye", StringComparison.OrdinalIgnoreCase))
        {
            Dismiss("bye");
            return;
        }

        Ask(text);
    }

    // Speaker ----------------------------------------------------------------------------------

    private void LoadSpeaker()
    {
        var key = string.IsNullOrWhiteSpace(config.AskSpeaker) ? SpeakerPresets.DefaultKey : config.AskSpeaker;
        if (speaker.Key == key && speaker.Name.Length > 0 && !speaker.Name.StartsWith(speaker.Source.ToString(), StringComparison.Ordinal))
            return;
        var parsed = SpeakerEntry.ParseKey(key) ?? (SpeakerSource.Npc, 1041316u);
        speaker = Sheets.Catalog.Find(key) ?? Fallback(parsed.Source, parsed.Id);
        if (speaker.Source == SpeakerSource.Self)
            speaker = speaker with { Name = PlayerName() };
    }

    /// <summary>"You (advising)" / "You" for Alice mode; the speaker's name otherwise.</summary>
    private string DisplayName(bool assistant)
    {
        if (speaker.Source == SpeakerSource.Self)
        {
            var name = PlayerName();
            return assistant ? $"{name} ({(string.IsNullOrWhiteSpace(config.AskSelfTitle) ? "advising" : config.AskSelfTitle)})" : name;
        }

        return assistant ? speaker.Name : "You";
    }

    private string PlayerName() => objects.LocalPlayer?.Name.TextValue is { Length: > 0 } n ? n : "You";

    private void TrySpawn()
    {
        ActorNote = "";
        if (!config.AskSpawnNpc)
        {
            ActorNote = "NPC off in settings";
            return;
        }

        if (Unsafe(out var why))
        {
            ActorNote = why;
            log.Information("XivDesktop ask: no NPC, dialogue only ({Why})", why);
            return;
        }

        var player = objects.LocalPlayer;
        if (player is null)
        {
            ActorNote = "not logged in";
            return;
        }

        try
        {
            NpcLook? look = null;
            NativeCharacter* copy = null;
            humanoid = true;
            switch (speaker.Source)
            {
                case SpeakerSource.Self:
                    copy = (NativeCharacter*)player.Address;
                    break;
                case SpeakerSource.Npc:
                    look = Sheets.NpcLookFor(speaker.Id);
                    humanoid = look?.IsHuman ?? false;
                    break;
                case SpeakerSource.Minion or SpeakerSource.Mount:
                    look = speaker.ModelCharaId > 0 ? NpcLook.Model(speaker.ModelCharaId) : null;
                    humanoid = false;
                    break;
                case SpeakerSource.Pet:
                    look = PetLook();
                    humanoid = false;
                    break;
            }

            if (copy == null && look is null)
            {
                ActorNote = speaker.Source == SpeakerSource.Pet
                    ? $"{speaker.Name}'s model is not known yet: summon it once and ask again"
                    : $"no appearance for {speaker.Name}";
                log.Information("XivDesktop ask: no NPC, dialogue only ({Why})", ActorNote);
                return;
            }

            var p = player.Position;
            follower.Spawn(p.X, p.Y, p.Z, player.Rotation);
            var pos = new Vector3((float)follower.X, p.Y, (float)follower.Z);
            actor = LocalActor.Spawn(look, copy, DisplayName(assistant: true), pos, (float)follower.Yaw, out var error);
            if (actor is null)
            {
                ActorNote = error;
                log.Warning("XivDesktop ask: spawning {Speaker} failed ({Error}); dialogue only", speaker.Key, error);
            }
            else
            {
                log.Debug("XivDesktop ask: spawned {Speaker} as a local character", speaker.Key);
            }
        }
        catch (Exception ex)
        {
            actor?.Dispose();
            actor = null;
            ActorNote = ex.Message;
            log.Warning(ex, "XivDesktop ask: spawning failed; dialogue only");
        }
    }

    /// <summary>A pet's model: learned earlier, or read now from your own summoned pet of that name.</summary>
    private NpcLook? PetLook()
    {
        if (config.AskPetModels.TryGetValue(speaker.Name, out var known) && known > 0)
            return NpcLook.Model(known);
        var player = objects.LocalPlayer;
        if (player is null)
            return null;
        foreach (var o in objects)
        {
            if (o is not IBattleNpc npc || npc.OwnerId != player.EntityId)
                continue;
            if (!string.Equals(npc.Name.TextValue, speaker.Name, StringComparison.OrdinalIgnoreCase))
                continue;
            var id = ((NativeCharacter*)npc.Address)->ModelContainer.ModelCharaId;
            if (id <= 0)
                continue;
            config.AskPetModels = new Dictionary<string, int>(config.AskPetModels, StringComparer.OrdinalIgnoreCase) { [speaker.Name] = id };
            save();
            log.Information("XivDesktop ask: learned {Pet}'s model ({Id}) from your summon", speaker.Name, id);
            return NpcLook.Model(id);
        }

        return null;
    }

    /// <summary>States where a spawned character must not exist (and where it is removed if it does).</summary>
    private bool Unsafe(out string why)
    {
        why = "";
        if (!clientState.IsLoggedIn)
            why = "not logged in";
        else if (clientState.IsGPosing)
            why = "in GPose";
        else if (clientState.IsPvP)
            why = "in PvP";
        else if (condition[ConditionFlag.InCombat])
            why = "in combat";
        else if (condition[ConditionFlag.OccupiedInCutSceneEvent] || condition[ConditionFlag.WatchingCutscene] || condition[ConditionFlag.WatchingCutscene78])
            why = "in a cutscene";
        else if (condition[ConditionFlag.BetweenAreas] || condition[ConditionFlag.BetweenAreas51])
            why = "changing zones";
        else if (condition[ConditionFlag.BoundByDuty])
            why = "in a duty";
        else if (condition[ConditionFlag.LoggingOut])
            why = "logging out";
        return why.Length > 0;
    }

    private void Despawn()
    {
        actor?.Dispose();
        actor = null;
        preview?.Dispose();
        preview = null;
    }

    /// <summary>Spawns <paramref name="e"/> beside you for a few seconds (the picker's preview).</summary>
    public string Preview(SpeakerEntry e)
    {
        preview?.Dispose();
        preview = null;
        if (Unsafe(out var why))
            return $"error: no preview {why}";
        var player = objects.LocalPlayer;
        if (player is null)
            return "error: not logged in";
        NpcLook? look = e.Source switch
        {
            SpeakerSource.Npc => Sheets.NpcLookFor(e.Id),
            SpeakerSource.Minion or SpeakerSource.Mount when e.ModelCharaId > 0 => NpcLook.Model(e.ModelCharaId),
            SpeakerSource.Pet when config.AskPetModels.TryGetValue(e.Name, out var m) => NpcLook.Model(m),
            _ => null,
        };
        var copy = e.Source == SpeakerSource.Self ? (NativeCharacter*)player.Address : null;
        if (look is null && copy == null)
            return $"error: no preview for {e.Name}";
        var r = player.Rotation - MathF.PI / 2f; // to the player's right
        var pos = player.Position + new Vector3(MathF.Sin(r) * 2.2f, 0, MathF.Cos(r) * 2.2f);
        preview = LocalActor.Spawn(look, copy, e.Name, pos, (float)Follower.YawTowards(pos.X, pos.Z, player.Position.X, player.Position.Z), out var error);
        previewTime = 8f;
        return preview is null ? $"error: {error}" : $"ok: previewing {e.Name}";
    }

    // Frame ------------------------------------------------------------------------------------

    private void OnUpdate(IFramework fw)
    {
        try
        {
            Tick((float)Math.Clamp(fw.UpdateDelta.TotalSeconds, 0, 0.1));
        }
        catch (Exception ex)
        {
            log.Error(ex, "XivDesktop ask: frame update failed; dismissing");
            try
            {
                Dismiss("error");
            }
            catch
            {
                // already logged
            }
        }
    }

    private void Tick(float dt)
    {
        if (preview is not null)
        {
            previewTime -= dt;
            if (previewTime <= 0 || !preview.Tick(dt) || Unsafe(out _))
            {
                preview.Dispose();
                preview = null;
            }
        }

        backend.Drain(OnStream);
        if (!summoned)
            return;

        if (actor is not null && Unsafe(out var why))
        {
            // Combat, cutscenes, zone changes…: the character goes; the dialogue stays for reading.
            log.Information("XivDesktop ask: NPC removed ({Why})", why);
            ActorNote = why;
            actor.Dispose();
            actor = null;
        }

        var answer = transcript.Turns.Count > 0 ? transcript.Turns[^1] : null;
        if (answer is { Role: TurnRole.Assistant })
            reveal.Advance(dt, answer.Text);
        var talking = transcript.Pending || (answer is { Role: TurnRole.Assistant } && !reveal.Caught(answer.Text));

        TickActor(dt, talking);
        TickPlayer(dt);
        Render();
    }

    private void OnStream(StreamEvent e)
    {
        switch (e.Kind)
        {
            case StreamEventKind.Delta:
            {
                var (text, found) = cues.Push(e.Text);
                transcript.Append(text);
                foreach (var c in found)
                    Queue(c);
                break;
            }

            case StreamEventKind.Done:
            {
                var (text, found) = cues.Flush();
                transcript.Append(text);
                foreach (var c in found)
                    Queue(c);
                transcript.Finish();
                if (!answerHadCue && config.AskEmotes && KeywordCues.Guess(lastQuestion) is { } g)
                {
                    Queue(new Cue(CueTarget.Npc, g.Npc));
                    if (g.Player is { } pl)
                        Queue(new Cue(CueTarget.Player, pl));
                }

                break;
            }

            case StreamEventKind.Error:
                cues.Flush();
                transcript.Fail(e.Text);
                log.Information("XivDesktop ask: {Error}", e.Text);
                break;
        }
    }

    private void Queue(Cue c)
    {
        answerHadCue = true;
        if (config.AskEmotes)
            pendingCues.Enqueue(c);
    }

    private void TickActor(float dt, bool talking)
    {
        if (actor is null)
            return;
        if (!actor.Tick(dt))
        {
            ActorNote = actor.Alive ? "the character never became drawable" : "the character was removed by the game";
            log.Information("XivDesktop ask: NPC gone ({Why}); dialogue only", ActorNote);
            actor.Dispose();
            actor = null;
            return;
        }

        var player = objects.LocalPlayer;
        if (player is null)
            return;
        var p = player.Position;
        var cam = CameraPosition();
        if (config.AskFollow)
            follower.Update(dt, p.X, p.Y, p.Z, player.Rotation, talking, cam.X, cam.Z);
        else
            follower.Update(dt, follower.X, p.Y, follower.Z, player.Rotation, true, double.NaN, double.NaN);
        actor.Place(new Vector3((float)follower.X, (float)follower.Y, (float)follower.Z), (float)follower.Yaw);

        // Locomotion wins; then talking/listening loops for humanoids.
        ushort loop = follower.Gait switch
        {
            Gait.Run => Timelines.Run,
            Gait.Walk => Timelines.Walk,
            _ when humanoid && speaker.Talks => talking ? Timelines.StandTalk : Timelines.StandListen,
            _ => 0,
        };
        actor.SetLoop(loop);

        while (pendingCues.Count > 0 && pendingCues.Peek().Target == CueTarget.Npc)
        {
            var c = pendingCues.Dequeue();
            if (follower.Gait != Gait.Idle)
                continue; // no emotes on the move
            if (humanoid && EmoteTable.Find(c.Emote) is { } e)
                actor.Play(e.TimelineId);
            else if (!humanoid)
                actor.Play(Timelines.IdleFidget); // minions and mounts react with their own fidget
        }
    }

    private void TickPlayer(float dt)
    {
        // Player cues (from the model or the keyword guess): local timelines only, never /emote.
        var player = objects.LocalPlayer;
        while (pendingCues.Count > 0 && pendingCues.Peek().Target == CueTarget.Player)
        {
            var c = pendingCues.Dequeue();
            if (player is not null && config.AskPlayerGestures && PlayerIdle() && EmoteTable.Find(c.Emote) is { } e)
                ((NativeCharacter*)player.Address)->Timeline.TimelineSequencer.PlayTimeline(e.TimelineId);
        }

        // Turn toward the speaker for a moment after asking, while standing still.
        if (turnPlayerFor <= 0 || player is null || actor is null)
            return;
        turnPlayerFor -= dt;
        if (!PlayerIdle())
        {
            turnPlayerFor = 0;
            return;
        }

        var want = Follower.YawTowards(player.Position.X, player.Position.Z, follower.X, follower.Z);
        var next = Follower.TurnToward(player.Rotation, want, 6.0 * dt);
        if (Math.Abs(Follower.Wrap(want - player.Rotation)) < 0.03)
        {
            turnPlayerFor = 0;
            return;
        }

        ((NativeObject*)player.Address)->SetRotation((float)next);
    }

    private void PlayerAsks()
    {
        if (!config.AskPlayerGestures || objects.LocalPlayer is not { } player || !PlayerIdle())
            return;
        turnPlayerFor = actor is not null ? 1.2f : 0;
        ((NativeCharacter*)player.Address)->Timeline.TimelineSequencer.PlayTimeline(Timelines.AskGesture);
    }

    private bool PlayerIdle() =>
        follower.PlayerSpeed < 0.3 && !condition[ConditionFlag.Mounted] && !condition[ConditionFlag.Casting]
        && !condition[ConditionFlag.InCombat] && !condition[ConditionFlag.Jumping] && !condition[ConditionFlag.InFlight]
        && !condition[ConditionFlag.Swimming] && !condition[ConditionFlag.Diving] && !condition[ConditionFlag.Occupied];

    private static Vector3 CameraPosition()
    {
        var cm = CameraManager.Instance();
        if (cm == null || cm->Camera == null)
            return new Vector3(float.NaN);
        return cm->Camera->CameraBase.SceneCamera.Object.Position;
    }

    private void Render()
    {
        if (view is null || !view.IsOpen)
            return;
        var entries = new List<(string, string, bool)>(transcript.Turns.Count + 1);
        var you = speaker.Source == SpeakerSource.Self ? PlayerName() : "You";
        var them = DisplayName(assistant: true);
        foreach (var t in transcript.Turns)
            entries.Add((t.Role == TurnRole.User ? you : them, t.Text.Length == 0 && !t.Complete ? "…" : t.Text, t.Role == TurnRole.User));
        if (entries.Count == 0)
            entries.Add((them, Greeting(), false));
        var last = transcript.Turns.Count > 0 ? transcript.Turns[^1] : null;
        var visible = last is { Role: TurnRole.Assistant } ? reveal.Visible : int.MaxValue;
        if (last is { Role: TurnRole.Assistant, Complete: false, Text.Length: 0 })
            visible = 1; // the "…" placeholder
        var arrow = last is { Role: TurnRole.Assistant, Complete: true } && reveal.Caught(last.Text);
        view.Show(entries, entries.Count > 0 && transcript.Turns.Count == 0 ? int.MaxValue : visible, arrow);
    }

    private string Greeting() => speaker.Source switch
    {
        SpeakerSource.Self => "Well? Out with it. You know I'm only going to tell you what you already know.",
        SpeakerSource.Minion or SpeakerSource.Mount => $"{speaker.Name} looks at you expectantly.",
        _ => ActorNote.Length > 0 && config.AskSpawnNpc ? $"What would you like to know? ({ActorNote})" : "What would you like to know?",
    };

    private void OnTerritoryChanged(uint territory)
    {
        if (actor is null && preview is null)
            return;
        // The game clears local characters on zone change; forget ours without touching the new zone's slots.
        actor?.Forget();
        actor = null;
        preview?.Forget();
        preview = null;
        if (summoned)
            ActorNote = "changed zones";
    }

    private void OnLogout(int type, int code) => Dismiss("logout");

    public void Dispose()
    {
        framework.Update -= OnUpdate;
        clientState.TerritoryChanged -= OnTerritoryChanged;
        clientState.Logout -= OnLogout;
        backend.Cancel();
        // Game objects and addons belong to the framework thread (inline when already on it).
        var t = framework.RunOnFrameworkThread(() =>
        {
            Despawn();
            view?.Close();
        });
        if (!t.Wait(TimeSpan.FromSeconds(2)))
            log.Warning("XivDesktop ask: timed out removing the NPC on unload");
    }
}
