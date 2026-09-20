# /npc: the speaker never appears — investigation, 2026-09-20

Symptom: `/npc` opens the dialogue ("Delivery moogle — What would you like to know?") but no
character is ever visible. Tested live in game with a Delivery Moogle (ENpc 1000063, ModelChara 15),
five builds, each reporting the spawned character's state to the Dalamud log.

| # | How the character was set up | What the game reported |
|---|---|---|
| 0 | as shipped: `CreateBattleCharacter`, write `ModelCharaId` (+ customize/gear for humans), wait for `IsReadyToDraw` | never ready: "the character never became drawable" after 5 s |
| 1 | `CopyFromCharacter(local player)`, then the NPC look, `DisableDraw` → `EnableDraw` | ready at once; kind Pc/4, model 15, drawObject present, **renderFlags 0x800**, not visible |
| 2 | same, waiting 0.35 s after `DisableDraw` and calling `EnableDraw` for 6 s while hidden | drawObject present **but `IsVisible` false**, renderFlags 0x800 |
| 3 | `SetupBNpc(row whose ModelChara == 15)` — nothing copied from the player | **no BNpcBase row uses ModelChara 15** (delivery moogles are event NPCs only) → fell back to #0 |
| 4 | `SetupBNpc(any ordinary creature row)` (row 2), then `ModelCharaId = 15`, redraw, clear 0x800 | kind BattleNpc/0, model 15, drawObject present, **`IsVisible` false**, renderFlags None, alpha 1 |

What this establishes
- A bare client character never builds a draw object; it must be initialised (`CopyFromCharacter` or `SetupBNpc`).
- 0x800 in `GameObject.RenderFlags` (ClientStructs names it `Nameplate`; Penumbra calls the same bit `IsLoading`)
  stays set forever on the copy path. Clearing it is not enough.
- Even with a clean `SetupBNpc` character, no flags set and full alpha, the draw object exists and stays
  invisible. So the remaining suspects are: (a) ModelChara 15 is not what the delivery moogle actually
  draws with (check the ENpcBase row: `ModelChara`, and whether the look comes from `ModelBody`/`NpcEquip`
  or a `Behavior`/event object instead); (b) swapping `ModelCharaId` after setup needs more than
  DisableDraw/EnableDraw (Brio and Glamourer go through a full redraw and also reset customize/equipment for
  non-human models); (c) overworld client objects at index 200+ are hidden by the game unless something
  else is done (compare with Brio's overworld spawning, which does work).
- The owner does NOT want the player copied onto the NPC (no gear, name or company tag): use `SetupBNpc`
  or build the look from sheets.

Next steps, in order
1. Try a speaker whose model a creature really uses (a minion or a mount: `Companion`/`Mount` → BNpcBase
   exact match → build #3's path). If that is visible, the pipeline is right and only model lookup for event
   NPCs is wrong.
2. Read how Brio spawns and redraws overworld actors and mirror it exactly (object kind, the redraw
   sequence, which draw-state bits it touches).
3. Dump the ENpcBase row for 1000063 to confirm what it really renders with.
4. UX: the dialogue box is drawn over the spot where the speaker stands (straight ahead of the player);
   move it to the bottom of the screen like the game's own Talk window.

`npc-spawn-investigation.patch` is the last build's diff against xivdesktop-dalamud master (diagnostic
logging, `SpeakerSheets.BNpcBaseForModel` / `AnyCreatureBase`, the `SetupBNpc` path, the settle/nudge
logic and the 0x800 clear). It is NOT committed anywhere. The logging alone is worth landing.
