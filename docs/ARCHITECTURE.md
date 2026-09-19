# Architecture

> v1 builds and its host tests pass. Nothing has been observed running in the game yet (v0 or v1). This
> describes the implemented design and the planned direction, not verified behaviour.

## Projects

| Project | Output | Depends on | Role |
| --- | --- | --- | --- |
| `src/XivDesktop.Core` | `XivDesktop.Core.dll` (net10.0) | BCL only | `.desktop` parsing, Exec → sh, visibility rules, icon lookup, search, favourites/recents, launch validation; v1: `Windows/` (ghostty Call JSON, window diff, window → app), `Input/` (chords, defaults, edge tracker), `Workspaces/`, `Palette/` (fuzzy, calculator, providers). Pure and host-tested. |
| `src/XivDesktop.Plugin` | `XivDesktop.dll` (Dalamud.NET.Sdk 15) | Core, Dalamud API 15 | Catalog, ghostty backends, session (workspaces, notifications), key chords, palette, app grid, settings, IPC provider. |
| `src/XivDesktop.Umbra` | `Umbra.XivDesktop.dll` | Umbra, IPC contract, three Core source files | "Apps" and "Windows" toolbar widgets. Talk to the plugin only over IPC. |
| `src/Shared/IpcContract.cs` | compiled into Plugin, Umbra and tests | — | Gate names and JSON payload shapes. Additive changes only. |
| `tools/scan` | console | Core | Runs the catalog against the host's real directories (no Wine). |
| `tests/XivDesktop.Core.Tests` | xunit v3 | Core, IPC contract | Sample `.desktop` files and temp directory trees. |

## Plugin

```
Plugin (IDalamudPlugin)
 ├─ Configuration          pluginConfigs/XivDesktop.json: v0 lists and options; v1 keybinds, pin args, workspaces, toggles
 ├─ CatalogService         AppCatalog.Scan on the thread pool; volatile immutable snapshot; Reload cancels a running scan
 ├─ GhosttyCallBackend     ILaunchBackend + IWindowManager over GhosttyDalamud.v1.Call; polls window.list ≤ every 250 ms
 │   └─ GhosttyPostBackend    fallback launch (window pull run) and /term lines (new, ask, toggle)
 ├─ DesktopService         resolve / launch / favourites / recents / status; any thread in, framework thread for writes
 ├─ SessionService         target panel, workspaces (WorkspaceModel → window.place hide), focus cycling, notifications
 ├─ KeybindService         IFramework.Update: IKeyState (GetAsyncKeyState fallback) → ChordTracker → CommandRunner
 ├─ CommandRunner          runs palette rows and key actions
 ├─ PaletteWindow          Super+D / /desktop; PaletteEngine over a PaletteContext snapshot
 ├─ LauncherWindow         /desktop apps: the v0 icon grid
 ├─ SettingsWindow         General, Keybinds, Windows
 └─ DesktopIpc             XivDesktop.v1.* gates; every handler catches its own exceptions
```

**Paths.** Core works in Linux paths and maps them through `HostPaths.ToLocal`. That is the identity on the
host, and `Z:` plus backslashes under Wine. `OperatingSystem.IsWindows()` picks the Wine mapping inside the
game. Resolved icon paths are stored in mapped form, because that is what `ITextureProvider` opens.

**Threads.** Scans never run on the framework thread: listing `Z:\` goes through Wine's file layer and costs
tens of milliseconds even on the host. Readers take the current `AppCatalog` reference, which is never
mutated. Favourites and recents are replaced as whole lists on the framework thread and saved there. IPC
`Launch` from another thread queues the backend call with `IFramework.RunOnFrameworkThread` and returns
straight away, so it cannot deadlock against the framework.

## How it layers on ghostty-dalamud

ghostty-dalamud exposes `GhosttyDalamud.v1.Status` (`Func<string>`) and `GhosttyDalamud.v1.Post`
(`Action<string>`, a `/term` command line without the prefix). XivDesktop uses only these:

1. `Post.HasAction` decides whether launching is enabled.
2. A launch posts `window pull run <sh command>`. ghostty-dalamud forwards it to ghostty-agent, which runs
   the command with `sh -c` inside its headless compositor (socket `ffxiv-0`). The first window it maps is
   shown as a pet.
3. ghostty-dalamud's status text is shown as-is. XivDesktop does not parse it.

What XivDesktop cannot know through `Post`: whether the agent is running with `--windows wayland`, whether
the command started, and which window it produced. This is the main limit of v0.

That was v0. v1 uses ghostty-dalamud's JSON gate, `GhosttyDalamud.v1.Call`, when it is registered
(ghostty-dalamud's `docs/IPC.md`), through `GhosttyCallBackend`:

- **Reads.** `window.list` on the framework thread at most every 250 ms. `rev` is compared first; the
  windows are parsed only when it moved, and `Changed(before, after)` fires. `agent.status` every 2 s.
- **Changes.** `window.open` (`run`, with an optional `pin`), `window.close`, `window.focus`,
  `window.place`. Each returns `{"queued": true, "request": N}` at once; the backend keeps the request ids it
  sent and resolves them from `window.list`'s `requests` (the new panel id of an open; refusals become
  `RequestFailed`).
- **Fallback.** Without the Call gate, launching goes through `GhosttyPostBackend` as in v0, and the window
  features (list, workspaces, actions, taskbar) report themselves unavailable.
- **/term lines** (`new`, `toggle`, `ask …`, `pin pet`) still go through `Post`: Call has no method for
  them.

XivDesktop does not register `/window`; ghostty-dalamud owns it. Its own command stays `/desktop`.

### What XivDesktop needs from ghostty-dalamud next

Found while building v1 against the documented IPC; each is a request, none is implemented there:

1. **Hidden state in `window.list`**: a `"hidden": true` field. Workspaces hide panels with
   `window.place {"pin": "hide"}` (`/term pin hide` in `lua/world.lua`), but the list still says
   `"kind": "pet"|"pin"`, so XivDesktop can only track what it hid itself. A dedicated
   `window.hide {"id": N, "hidden": true|false}` would also make this a documented use instead of a pin
   argument.
2. **Pin where it is**: `window.place {"id": N, "pin": "stay"}` (or similar) that turns a pet into a
   world pin at its current position and facing. Today the pet → pin toggle has to use `here`, 2.5 yalms
   in front of the character.
3. **Focus without the keyboard**: `window.focus {"id": N, "keyboard": false}` to raise/select a panel
   while the game keeps the keyboard, so Super+Left/Right can cycle without losing the key chords.
4. **A terminal over Call**: `terminal.open {"profile": N, "pin": "pet", "run": "cmd"}`, ordered, returning
   the panel id. It would give Super+Enter a world terminal regardless of the dropdown's state, and let
   `Terminal=true` apps run.
5. **The Wayland app id**: `window.list` entries with the toplevel's `app_id` (and PID), so icons do not
   have to be guessed from the command's program name and the title.

## Later

Implemented in v1 on top of `Call` (unverified in game): the window list, focus/close/place from the
palette, keys, IPC and the Umbra taskbar, workspaces, open/end notifications, and agent status in
Settings → Windows.

Planned, **not implemented**:

- **Layouts per zone.** `window.place` would let a set of placements be saved per territory and restored
  on zone change.
- Notifications *from apps*: freedesktop `org.freedesktop.Notifications` from apps in the compositor, shown
  as Dalamud notifications. This needs the agent to own a notification daemon. (v1 only notifies about
  panels opening and ending.)
- Clipboard between the game (Windows clipboard under Wine) and the compositor.
- File drops: open a file from an in-game picker with the app's `%f`/`%u` field codes. The Exec parser
  already knows where they go; v0 drops them.
- Saved layouts: sets of apps and placements, per zone or named.
- MCP tools in xiv-mcp (`list_apps`, `launch_app`, `list_windows`, `window_action`, `switch_workspace`,
  `palette_search`) on top of `XivDesktop.v1.*`; the gates exist, the tools do not.
- Desktop integrations (GNOME Shell extension, KDE later): see the README.
- Terminal=true apps, launched in a ghostty tab once a single ordered call can open a tab with a command.
- SVG icons, without adding a heavy dependency (for example, rasterised once on the host by ghostty-agent).
