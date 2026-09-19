# Architecture

> v0 builds and its host tests pass. Nothing has been observed running in the game yet. This describes
> the implemented design and the planned direction, not verified behaviour.

## Projects

| Project | Output | Depends on | Role |
| --- | --- | --- | --- |
| `src/XivDesktop.Core` | `XivDesktop.Core.dll` (net10.0) | BCL only | `.desktop` parsing, Exec → sh, visibility rules, icon lookup, search, favourites/recents, launch validation. Pure and host-tested. |
| `src/XivDesktop.Plugin` | `XivDesktop.dll` (Dalamud.NET.Sdk 15) | Core, Dalamud API 15 | Catalog service, launch backend, `/desktop` window, IPC provider. |
| `src/XivDesktop.Umbra` | `Umbra.XivDesktop.dll` | Umbra, IPC contract, two Core source files | "Apps" toolbar widget. Talks to the plugin only over IPC. |
| `src/Shared/IpcContract.cs` | compiled into Plugin, Umbra and tests | — | Gate names and JSON payload shapes. Additive changes only. |
| `tools/scan` | console | Core | Runs the catalog against the host's real directories (no Wine). |
| `tests/XivDesktop.Core.Tests` | xunit v3 | Core, IPC contract | Sample `.desktop` files and temp directory trees. |

## Plugin

```
Plugin (IDalamudPlugin)
 ├─ Configuration          pluginConfigs/XivDesktop.json: Favourites, Recents, HomeOverride, IconSize, CloseOnLaunch
 ├─ CatalogService         AppCatalog.Scan on the thread pool; volatile immutable snapshot; Reload cancels a running scan
 ├─ ILaunchBackend         GhosttyPostBackend today (see below)
 ├─ DesktopService         resolve / launch / favourites / recents / status; any thread in, framework thread for writes
 ├─ LauncherWindow         /desktop ImGui window; textures via ITextureProvider.GetFromFile(Z:\...png) for visible cells only
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

## How it layers on ghostty-dalamud today

ghostty-dalamud exposes `GhosttyDalamud.v1.Status` (`Func<string>`) and `GhosttyDalamud.v1.Post`
(`Action<string>`, a `/term` command line without the prefix). XivDesktop uses only these:

1. `Post.HasAction` decides whether launching is enabled.
2. A launch posts `window pull run <sh command>`. ghostty-dalamud forwards it to ghostty-agent, which runs
   the command with `sh -c` inside its headless compositor (socket `ffxiv-0`). The first window it maps is
   shown as a pet.
3. ghostty-dalamud's status text is shown as-is. XivDesktop does not parse it.

What XivDesktop cannot know through `Post`: whether the agent is running with `--windows wayland`, whether
the command started, and which window it produced. This is the main limit of v0.

All of this sits behind `ILaunchBackend` (`Name`, `Available`, `MissingMessage`, `StatusText()`,
`Launch(shellCommand)`). ghostty-dalamud is adding a JSON gate, `GhosttyDalamud.v1.Call`
(`{"method": "window.list|window.open|window.close|window.focus|window.place|agent.status", "params": {}}`).
Moving to it means writing one `GhosttyCallBackend` class and choosing it in `Plugin.cs`. The launcher, IPC
and commands stay as they are. ghostty-dalamud is also getting a top-level `/window` command. XivDesktop
does not register `/window`; its command stays `/desktop`.

## Later

What a window-level API (the `Call` gate above, or `GhosttyDalamud.v1.Window.*`) would add. None of it is
implemented:

- **Window list.** `window.list` would let XivDesktop connect a launch to its window (by app id or PID) and
  show what is open.
- **Placement and layouts per zone.** `window.place` (pet, pinned at a world position, orbit) would let a
  layout be saved per territory and restored on zone change.
- **Focus.** `window.focus` / `window.close` from the launcher and from IPC.
- **Agent status.** `agent.status` would tell the launcher whether the compositor is actually up, instead
  of only whether the IPC gate exists.

Planned creature comforts, **not implemented**:

- A taskbar of open windows (Umbra widget and/or a DTR entry), built on the window list.
- Notifications: freedesktop `org.freedesktop.Notifications` from apps in the compositor, shown as Dalamud
  toasts. This needs the agent to own a notification daemon.
- Clipboard between the game (Windows clipboard under Wine) and the compositor.
- File drops: open a file from an in-game picker with the app's `%f`/`%u` field codes. The Exec parser
  already knows where they go; v0 drops them.
- Saved layouts: sets of apps and placements, per zone or named.
- MCP tools in xiv-mcp (`list_apps`, `launch_app`, window tools) on top of `XivDesktop.v1.*`.
- Terminal=true apps, launched in a ghostty tab once a single ordered call can open a tab with a command.
- SVG icons, without adding a heavy dependency (for example, rasterised once on the host by ghostty-agent).
