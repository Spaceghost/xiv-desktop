# Ask an NPC

> **Status.** Builds against Dalamud API 15 and its pure parts have host tests. **Nothing here has been
> observed in the game**: not the spawned character, its appearance, following or animations, the player's
> gestures, the KamiToolKit dialogue or picker, or the gateway streaming from inside Wine. Treat this page as
> the design and the reasons for it, not as a result.

`/npc` summons a speaker next to your character and opens a Talk-style dialogue at the bottom of the
screen. You type a question, your character turns to the speaker and gestures, the answer types out at the
game's pace while the speaker plays a talking loop, and you can keep asking follow-ups. ghostty-dalamud's own `/ask` (and `/term ask`) is
unchanged: a terminal or glass chat panel on the host side. This is the in-world version.

| Command | Does |
| --- | --- |
| `/npc <question>` | summons the speaker if needed and asks |
| `/npc` | summons it for a conversation |
| `/npc bye` | dismisses it (also Esc in the dialogue, or typing `bye`) |
| `/npc as <who> [question]` | `scholar`, `moogle`, `archivist`, `myself`, a key (`npc:1040124`, `minion:3`, `mount:1`, `pet:6`, `self`) or a favourite by name switches speaker; anything else opens the picker with that search |
| `/npc who` | opens the speaker picker |
| `/desktop ask …` | the same, from XivDesktop's own command |

**Why `/npc` and not `/ask`.** ghostty-dalamud owns `/ask` (its glass chat panel, `almanac --thread`).
Dalamud gives a command to whichever plugin registered it first and has no hand-over, so taking it from a
loaded plugin is not possible. Settings → Ask → "Route /ask to the NPC as well" (off by default) tries to
register `/ask` at plugin load and, when another plugin already holds it, logs that and says so in settings;
`/npc` always works. That is the least fragile option: no racing to load first, no IPC that ghostty would
have to implement, no command stolen from under a running plugin.

IPC: `XivDesktop.v1.Ask` (`Func<string, string>`) takes exactly what follows `/npc` and returns
`"ok: …"` or `"error: …"` at once; the answer streams into the dialogue.

## Backend: almanac's gateway, transcript kept here

`almanac` on the host has `ask` (one shot), `chat` (an interactive terminal loop) and `gateway`. None of
them keeps a conversation that another process can continue: `ask`/`chat` have no session id, and the
gateway (`almanac/gateway.py`) is a stateless, authenticated proxy in front of Ollama that speaks the
OpenAI chat-completions, OpenAI responses and Anthropic messages protocols and streams the upstream body
through unchanged.

So XivDesktop keeps the transcript **client-side** and resends it on every follow-up:

- `POST http://127.0.0.1:41881/v1/chat/completions` with `stream: true`, `model: "local"` (the gateway maps
  it to its default model) and `reasoning_effort: "none"` (no thinking delay). The plugin runs under Wine;
  loopback reaches the host.
- Bearer token from `~/.config/almanac/token`, read through the same home detection XivDesktop uses for the
  catalog (`Z:\home\<user>\.config\almanac\token` under Wine). It is read per request and never logged.
- The SSE body is parsed on the thread pool (`ChatStreamParser`: any chunking, UTF-8 split across reads,
  CRLF, comments, `[DONE]`, error payloads, Anthropic events too) and queued; the framework thread drains it.
- History is trimmed from the oldest turns to about 12 000 characters and always starts on a question.
  Failed answers are not resent.

Trade-off: the gateway is the bare model. `almanac ask` also gives the model almanac's knowledge base and
tools; the in-world speaker does not have them. Settings → Ask → Backend can switch to "ghostty terminal",
which posts `/term ask <question>` (one shot, terminal look) instead.

## The speaker: a client-side character

`LocalActor` allocates a character in the ClientObjectManager's local slots and deletes it again. The
server never learns about it; nothing is sent. The call sequence follows how posing tools do it, read from
Brio (Etheirys/Brio, `Game/Actor/ActorSpawnService.cs` and `ActorRedrawService.cs`, GPL-3.0; studied, not
copied) against the FFXIVClientStructs that ships with Dalamud API 15:

1. `ClientObjectManager.Instance()->CreateBattleCharacter()` → index (`0xFFFFFFFF` = no slot), then
   `GetObjectByIndex`.
2. Appearance, before drawing:
   - NPC: from its ENpcBase row: `ModelContainer.ModelCharaId`, the 26 customize bytes (the sheet's columns
     are in the game's customize order), ten `EquipmentModelId`s and two weapons (NpcEquip's gear when the
     row names one). A row with a ModelChara and no race (a moogle) gets only the model.
   - Minion / mount: the Companion.Model / Mount.ModelChara row.
   - Summon: the Pet sheet has names but no model link, so a pet's ModelChara is learned from your own
     summoned pet of that name (object table, `OwnerId` = you) and remembered in the config.
   - Myself: `CharacterSetup.CopyFromCharacter(localPlayer, WeaponHiding)`: race, customize, gear and weapons,
     no mount or companion.
3. Name written, targetable flag cleared (a client-only object must not become a target the server hears
   about), position 2 yalms in front of you facing you, `Alpha = 0`.
4. Each frame until `IsReadyToDraw()` (5 s at most): then `EnableDraw()` and a 0.6 s fade-in.
5. `DeleteObjectByIndex` on dismiss, combat, cutscene, zone change, GPose, PvP, logout or unload. It is
   never persisted. After a zone change the game has already cleared its local characters, so the
   reference is dropped without deleting.

Brio spawns in GPose; spawning outside GPose is less trodden. Every step is checked, and if anything fails
(no slot, no appearance, never drawable, a duty, combat…) the dialogue opens without a speaker and the log
says why (`XivDesktop ask: no NPC, dialogue only (…)`). Settings → Ask shows the same note.

### Following and animation

`Follower` (Core, tested) is the pet feel from ghostty-dalamud's `lua/world.lua` `M.pet`: a critically
damped spring toward a slot beside you (on the side away from the camera line, never behind you), kept out
of your personal space and off the camera→player line. It stays at the conversation spot until you walk
away; it faces its direction of travel while moving and faces you when you stop or while it talks.

Loops are `Timeline.BaseOverride`: walk (`normal/walk`, 13) and run (`normal/run`, 22) by its own speed;
when standing, humanoids loop `event_base/event_base_stand_talk` (799) while the answer types out and
`event_base/event_base_stand_listen` (801) otherwise. Gestures are `TimelineSequencer.PlayTimeline`.

When you ask, your character turns toward the speaker (local `SetRotation`, as ghostty's pet facing does)
and plays `event/event_talk1` (763) as a local timeline, only while you stand still and are not mounted,
casting, jumping, swimming or in combat.

### Emote cues

The system prompt asks the model to start with `[emote:<name>]` and optionally `[player:<name>]`.
`CueFilter` strips those tags from the streamed text (even split across chunks; unknown tags stay text) and
drops inline `<think>` blocks. Without a tag, a keyword guess on your line decides (thanks → bow, jokes →
laugh, frustration → comfort, greetings → wave, …).

Only a whitelist of conversational emotes can play (`EmoteTable`): wave, goodbye, bow, laugh, comfort,
think, surprised, yes (nod), no, cheer, clap, beckon, shrug, joy, welcome, converse, me, point, thumbsup,
huh, upset, blush, pray. Each Emote row id and its `ActionTimeline[0]` were read with xiv-mcp's sheet tools.
They play as local timelines on the speaker and on your character; the `/emote` command is never used.
Minions and mounts do not talk: they react with their idle fidget (`normal/idle_inactive1`, 4); there is no
per-minion emote mapping.

## Speakers

Presets (ENpcResident/ENpcBase rows checked with xiv-mcp): `scholar` = Studium researcher (1041316, the
default), `moogle` = delivery moogle (1000063, ModelChara 15), `archivist` = approachable archivist
(1040124), `myself`.

"Myself" is Alice mode: a copy of your character stands facing you, both lines carry your name ("You
(advising)" — the title is a setting), and the model is told to be your own inner advisor, speaking to you
in the second person.

The picker (`/npc who`, or Settings → Ask) searches every named ENpcResident with a drawable ENpcBase
(deduplicated by name and title, with a location from the Level sheet), your unlocked minions
(`UIState.IsCompanionUnlocked`), unlocked mounts (`PlayerState.IsMountUnlocked`), the job summons (Pet
sheet) and yourself. It has favourites (★), recents, and a live preview that stands the selection beside
you for eight seconds. The sheet scan runs once per session on the thread pool; only the unlock checks run
on the framework thread.

## UI

The dialogue and picker are native addons built with **KamiToolKit** 2.2.48 (NuGet, MIT, built for Dalamud
API 15; BetterFriendList, StatusTimers and Simple Tweaks bundle their own copies). The dialogue hides the
addon's window frame and draws the game's Talk balloon texture behind a name plate, a scrolling history
(older turns dimmed, your lines labelled), the newest answer typing out (`TextReveal`: 20–70 characters a
second by the reading-speed setting, short holds after punctuation; Enter on an empty line skips), a
blinking ▼ when it is complete, and a native text input "Ask <name>…": Enter sends, Esc dismisses. A native
input only has the keyboard while it is focused, so game hotkeys are not taken otherwise, and the game's
own cursor is used throughout.

If KamiToolKit fails to start, the dialogue falls back to an ImGui window that draws the same Talk texture
(`ITextureProvider.GetFromGame`), sets `NoMouseCursorChange` only while hovered and never sets an ImGui
cursor. The picker then lives only in Settings → Ask.

The balloon and name-plate texture coordinates in `TalkStyle` are placeholders until checked against
`ui/uld/Talk.uld` in game: the look is the least verified part.

## Settings (Settings → Ask)

Speaker (presets, picker), route /ask, NPC on/off, follow, emote cues, player gestures, reading speed, "Myself" title,
backend (gateway or ghostty terminal), gateway URL and model.

## Risks

- Local characters outside GPose: a game patch that changes ClientObjectManager or the draw path could
  crash the client. The code checks every pointer and slot, but it has not run in the game.
- Timeline ids are sheet rows; an ActionTimeline that does not exist for a skeleton (monsters, minions)
  simply does not play, which is why non-humanoids only get idle/walk/run and a fidget.
- Turning your character is a local rotation change; like ghostty's pet facing, the next movement update
  may carry it to the server. Nothing else leaves the client.
- The answer comes from a local model with no tools or knowledge base.
