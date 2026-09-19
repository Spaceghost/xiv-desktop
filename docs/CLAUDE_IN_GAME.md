# Claude in game

> **Status: this is the implemented design, not verified behaviour.** The Core layer is host-tested
> (protocol, event parsing into cards, permission state, the raw toggle, looks, sessions, palette rows),
> and the permission contract below was confirmed against a real `claude` on this host. Nothing has been
> observed running inside FFXIV. What is *not* implemented is listed at the end.

`/claude` is a Claude Code session you talk to in game. Its activity — tool calls, edits, commands,
results — is rendered as game UI rather than terminal text, and each session stands beside you as its own
NPC.

```
 FFXIV                                              the host
 ┌──────────────────────────────────┐  TCP v4  ┌───────────────────────────────┐
 │ ClaudeWindow    tool cards, raw  │  JOPEN → │ ghostty-agent                 │
 │ ClaudePermission  the dialog     │ ← JOUT   │   agent/jobs.nelua (pipes)    │
 │ ClaudeService   sessions, NPCs   │  JDATA → │   agent/claude.nelua (@claude)│
 │ McpHttpServer   127.0.0.1:88xx   │ ← JEXIT  │        ↳ claude -p …          │
 └──────────────────────────────────┘          └───────────────────────────────┘
          ↑ HTTP: the permission prompt tool, back from the job
```

## Transport

XivDesktop talks to `ghostty-agent` itself, not through ghostty-dalamud's IPC. Host and port come from the
settings and default to ghostty's own `127.0.0.1:7777`; the token is read from
`~/.config/ghostty-agent/token` (`Z:\home\<user>\…` under Wine, through the repository's existing home
detection). **The token is used for the `HELLO` frame and nothing else: it is never logged, never written
to the plugin configuration, and never part of an error message.**

The frames are ghostty-dalamud's protocol version 4 (`docs/JOBS.md` there): `JOPEN`/`JDATA`/`JCLOSE`/
`JSIG`/`JATTACH` out, `JOPENED`/`JOUT`/`JEXIT` back, each `u8 type, u32 little-endian length, payload`.
Everything the panel needs sits behind one interface, `IJobTransport`, so a different runner only has to
implement that.

XivDesktop hardcodes no `claude` flags. A session opens with argv `["@claude", "<json options>"]` and the
agent expands it; the options carry the model, the working directory, `resume`, the MCP config and any
extra arguments.

## Sessions are hard to kill

* Jobs are opened with the **keep** flag, so a session outlives the plugin unloading, a `/term reload`, a
  game crash or a restart.
* The connection reconnects with backoff, and every reconnect re-attaches the jobs the plugin still has.
  The agent replays its 256 KiB ring; the panel drops the prefix it has already rendered (by line, not by
  byte, so nothing new is ever lost) and carries on.
* Each session's id, name, working directory, model, title and last activity are stored in the plugin
  configuration. `/claude resume <name>` restarts a finished conversation with `--resume <session-id>`.
* Closing the panel **hides** a session. Only "End session" (`/claude end`) kills the job.

## Several Claudes at once

Each session is its own panel tab, its own NPC and its own permission scope: `/claude new [name]`,
`/claude list`, `/claude <name> <prompt>`, `/claude resume`, `/claude stop`, `/claude end [name]`. Six may
be open at once, and the panel warns once three or more are working at the same time. Super+D gets a row
for `claude <prompt>`, one per open session and one per remembered session.

## Permissions

**The route that works is `--permission-prompt-tool`.** It is not in `claude --help` on 2.1.278, but the
CLI accepts it, and it was confirmed end to end on this host by serving the tool and watching the traffic:

* The job is started with `--permission-prompts host`, an inline `--mcp-config` naming one HTTP server,
  and `--permission-prompt-tool mcp__xivdesktop__permission_prompt`.
* The client posts `server/discover` (MCP 2026-07-28), falls back to `initialize` (2025-11-25), then
  `notifications/initialized` and `tools/list`.
* `tools/call` arrives as
  `{"name":"permission_prompt","arguments":{"tool_name":"Write","input":{…},"tool_use_id":"toolu_…"}}`.
* The reply must be one text content block holding
  `{"behavior":"allow","updatedInput":{…}}` or `{"behavior":"deny","message":"…"}`. An allow runs the tool
  with `updatedInput`; a deny is reported to the model and recorded in the result line's
  `permission_denials`. Both were observed: the allowed write happened, the denied one did not.

The MCP server is an `HttpListener` bound only to `127.0.0.1` — Wine shares the host's network stack, so
the game's listener is the host's loopback. Each session gets its own random secret, sent in the
`X-XivDesktop-Auth` header; a request without a secret this run handed out is refused, so nothing else on
the machine can answer the player's prompts.

The dialog offers **Allow once / Allow for this session / Always for this tool / Deny**, with the tool's
arguments listed. "Always" is the only choice that is persisted. A prompt nobody answers is **denied**
after five minutes rather than left hanging, and a session going away denies everything it had queued.

**Fallback.** If the listener cannot come up, the session runs with `--permission-mode <configured>` and
`--permission-prompts none` — which *denies* anything that would still have prompted. The panel header
carries a warning and the notice says exactly what is in force. Nothing is ever auto-approved silently.

## What the panel renders

| stream-json event | rendered as |
| --- | --- |
| `system` / `init` | the header: session id, model, working directory, permission mode |
| `stream_event` / `content_block_delta` | the reply, as it is written |
| `assistant` text | a reply row (skipped when the deltas already said it) |
| `assistant` thinking | a collapsed row |
| `assistant` `tool_use` | a card: icon per kind (edit, read, run, search, web, task, plan, mcp), the target |
| `user` `tool_result` | the card's status and result line: `+12 −3`, `exit 0 · 14 lines`, `3 lines`, `14 matches` |
| `result` | the totals row: tokens in/out and cost |
| `rate_limit_event`, `system` others | a notice, when there is something to say |
| anything unknown, or not JSON | kept for the raw toggle, never shown as noise |

Long output is collapsed to six lines with "… N more lines (raw)"; the **raw** toggle (or Ctrl+R) shows
the job's stdout exactly as a terminal would have printed it. Enter sends, Esc closes the panel, Ctrl+C
and the Stop button send `SIGINT` to the job, which ends the turn without ending the session.

## The NPC

Each session has a deterministic look, rolled from its name with a stable hash and a stable PRNG, so the
same session always summons the same person and `/claude look <name> [seed]` rerolls it repeatably. The
roll picks a race, tribe, face, hair and colours from curated ranges — every one exists for that race —
plus one of four kits (scholar, artificer, archivist, adventurer). The nameplate reads
`Claude — <session name>`.

Once per new session the model is asked, in one short JSON answer, for its own name and look; when it
parses, it is applied on top of the roll, and when it does not (or it refuses, or it chatters) the roll
stands. That exchange is kept out of the conversation.

The director maps the session's state onto a pose: typing while working, reading while it reads or
searches, **looking up when it needs a decision**, a nod when a turn finishes, a shrug when something
broke. Each pose change plays its emote once.

The spawning is the `/npc` assistant's, reused rather than rewritten: `Ask/LocalActor.cs` allocates a
client-side character in the ClientObjectManager's local slots, dresses it, places it and deletes it again
(the server never hears of it), `Core/Ask/Follow.cs` keeps it beside the player without crowding the
camera, and `Core/Ask/EmoteTable.cs` supplies the animation timelines. `ClaudeAvatars` drives one actor and
one follower **per session**, each standing a little further round so several Claudes do not overlap, and
the nameplate is the object's own name (`LocalActor.SetName`) rather than a client-side plate.
`INpcAvatar` remains the seam, so Core stays free of game types and the panel works with no avatar at all.

**Not observed in the game.** None of the spawning, following or emoting above has been seen running —
that applies to the `/npc` assistant's own code as much as to this use of it.

## Settings

Settings → Claude: enable, the agent's host/port/token path, the working directory, the model, the
executable, extra arguments, streaming, the fallback permission mode, the always-allowed tools (with a
"forget them" button), the NPC and the self-description question, and the remembered sessions.

The working directory defaults to the home directory and decides what Claude can touch, so it is always on
the panel's header; the button beside it lists the home directory and every repository under it (the
directories with a `.git`, three levels deep, skipping build output and hidden directories).

## Not implemented / not verified

* Nothing has run in the game: no panel, no dialog, no NPC, no session.
* The avatar reuses the `/npc` assistant's spawner, which is itself unverified in game.
* The agent side's `@claude` runner has not been driven end to end with a real `claude` session by this
  repository's tests either; ghostty-dalamud's own `docs/JOBS.md` says the same.
* `JATTACH` replay handling is written against the documented behaviour and exercised only by unit tests
  with synthetic lines.
