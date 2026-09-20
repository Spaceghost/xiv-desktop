# xiv-desktop

**XivDesktop** is a Dalamud plugin that lists the Linux host's desktop applications inside FINAL FANTASY
XIV and opens them as windows in the game world. It does not draw or stream any windows itself. It sits on
top of ghostty-dalamud: that plugin's `ghostty-agent` runs a
headless Wayland compositor on the host and shows its windows as world panels. XivDesktop adds the parts a
desktop has and a terminal command does not: an app catalog with icons, search, favourites and recents, a
keyboard-first launcher palette (Super+D), sway-style key chords, nine workspaces of window panels, open/close
notifications, IPC for other plugins and MCP, and two optional Umbra toolbar widgets ("Apps" and a "Windows"
taskbar).

> **Status (v1.1).** The solution builds against Dalamud API 15 (.NET 10). The pure logic (catalog, palette
> ranking, calculator, key chords, workspaces, ghostty's `window.list` JSON) has host-side tests, and the
> scanner has been run against a real Bazzite host's application directories (Linux paths, not through
> Wine). **Nothing has been observed in the game yet**, v0 or v1: not the palette, the key chords (including
> whether the game or Wine reports the Windows keys at all), the Call backend against a running
> ghostty-dalamud, workspace hiding, notifications, the IPC gates, or either Umbra widget. Treat everything
> below about in-game behaviour as the design, not as a result.

## How it fits together

```
Super+D palette ─┐
key chords ──────┤
Umbra widgets ───┼─ XivDesktop.v1.* IPC ─> XivDesktop plugin (in game, under Wine)
xiv-mcp, plugins ┘                           ├─ catalog: reads Z:\usr\share\applications ... off the framework thread
                                             ├─ icons: Z:\...\hicolor\256x256\apps\*.png -> ITextureProvider
                                             ├─ windows: GhosttyDalamud.v1.Call window.list (polled ≤ 4×/s, by rev)
                                             └─ launch/close/focus/place: GhosttyDalamud.v1.Call window.*
                                                (fallback: GhosttyDalamud.v1.Post "window pull run <cmd>")
                                                          │
ghostty-dalamud (in game) ── world panels: pets and pins ─┘
        │
ghostty-agent (Linux host) ── sh -c <cmd> inside its compositor (socket ffxiv-0) ──> window shown as a pet
```

Wine maps the Linux root to drive `Z:`, so the plugin reads `.desktop` files and icons straight from the
host filesystem. The command itself runs on the host, not in Wine, because ghostty-agent starts it.
Details: [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md).

## Requirements

- XIVLauncher.Core with Dalamud API 15, and the .NET 10 SDK on the host (`~/.dotnet/dotnet` is picked up
  automatically). Reference assemblies come from `~/.xlcore/dalamud/Hooks/dev/`; rebuild after every
  Dalamud update.
- ghostty-dalamud, loaded, with `ghostty-agent` running with `--windows wayland`. Without it the launcher
  still lists apps but says *needs ghostty-dalamud (loaded, with ghostty-agent running --windows wayland)*
  and launching is disabled. XivDesktop can only see whether ghostty-dalamud's IPC is registered. It cannot
  see whether the agent is running, so a launch that ghostty accepts may still produce no window.

## Install (from the plugin repository)

XivDesktop is listed in the author's own third-party Dalamud repository, next to the
other FFXIV mods there. In game:

1. `/xlsettings` → **Experimental** → **Custom Plugin Repositories** → paste
   `https://spacegho.st/mods/ffxiv/plugins.json` → **+** → **Save and close**.
2. `/xlplugins` → **All Plugins** → search **XivDesktop** → **Install**.

While XivDesktop only has test builds, it shows up only for players who opted in:
`/xlsettings` → **Experimental** → **Get plugin testing builds**. Once it has a
stable release, ticking testing on its own entry is enough to get test builds early.
<https://spacegho.st/mods/ffxiv/plugins/> walks through the same steps. It is a
third-party repository: Dalamud will say nobody but the author reviewed it, which is
true. Releases are built on GitHub Actions from a tag (`.github/workflows/release.yml`);
`v1.2.3` is a stable release, `v1.2.3-test.1` moves the floating `testing` release.

You do not need any of that to run it: building it yourself, below, is the path the
author develops on, and it stays supported for anyone who wants to read the code first.

## Install (dev plugin)

```sh
tools/install-dev.sh
```

It builds Release and copies the result into `~/xiv-desktop-build/devplugin/`, so Dalamud never sees a
half-written build. It then prints the Wine path of the staged DLL, e.g.
`Z:\home\<you>\xiv-desktop-build\devplugin\XivDesktop.dll`. In game, once:

1. `/xlsettings` → **Experimental** → **Dev Plugin Locations**: paste that path, click **+**, then **Save and close**.
2. `/xlplugins` → **Dev Tools** → **Installed Dev Plugins**: Dalamud adds dev plugins **disabled**. Enable
   **XivDesktop**, and in its entry tick **Start on boot**.
3. `/desktop` opens the launcher.

After rebuilding, run `tools/install-dev.sh` again and reload XivDesktop from `/xlplugins`. The script never
edits Dalamud's configuration and never copies anything into `~/.xlcore`.

## In game

| Command | What it does |
| --- | --- |
| `/desktop` | toggle the launcher palette (also Super+D) |
| `/desktop <text>` | open the palette with a query already typed |
| `/desktop apps [search]` | the v0 app grid with icons, favourites and recents |
| `/desktop settings` | settings: general, keybinds, windows and workspaces |
| `/desktop ws <1-9>` | switch workspace |
| `/desktop windows` | list the window panels in chat |
| `/desktop launch <id or search>` | launch an app by desktop-file id (`org.gnome.TextEditor`), exact name, or best search match |
| `/desktop reload` | rescan the application directories (runs off the framework thread) |
| `/desktop status` | one line: app count, and whether ghostty-dalamud is there |

The app grid (`/desktop apps`, or *App grid* in the palette) has a search box (focused when the window
opens), the favourites and recent rows (shown while the search is empty), and an icon grid. **Up/Down** move by row. **Left/Right** move by one while the search
box is empty or unfocused. **Enter** launches the selection. Clicking a cell launches it. Right-click gives
*Add to favourites*, *Launch* and *Copy command*. The window closes after a launch. Favourites and recents
are stored in `pluginConfigs/XivDesktop.json`.

With ghostty-dalamud's `GhosttyDalamud.v1.Call` gate a launch is `window.open {"run": <command>}`, and the
new panel's id comes back, so its icon and notifications are known for sure. Without it, a launch posts
`window pull run <command>`, the same as typing `/term window pull run <command>`. Either way the new window
appears as a pet beside the character.

## Launcher palette (Super+D)

A centred, keyboard-first palette in the style of Walker, Vicinae or Raycast: one query box, and ranked
rows from several providers. Each row has an icon, a title (matched characters highlighted and underlined),
a subtitle and a provider badge.

| Provider | Rows | Enter does |
| --- | --- | --- |
| **App** | the catalog; favourites and recents rank higher and fill the empty query | launch |
| **Panel** | every ghostty panel from `panel.list`: terminals, windows, adopted windows, chat; kind badge, view (dropdown, tab, pet, pin, hud, min, full, hidden), title. Ranked first on an empty query and alone with `w:` | `panel.focus` (a window on another workspace switches there first) |
| **Window** | only on a ghostty-dalamud without `panel.list`: window panels from `window.list` | focus |
| **Action** | close window, pin here, make pet, workspace N, move to workspace N, reload apps, app grid | run it on the target panel |
| **Calc** | arithmetic (`2+2`, `sqrt 2`, `2^10 % 7`, `max(3, 9)`, `2*pi`) | copy the result to the clipboard |
| **Command** | `/term`, `/term new`, `/term pin pet`, `/window pull`, `ask <question>`, any `/command` | post it to ghostty-dalamud, or run the command |

Prefixes narrow it to one provider: `a:` apps, `w:` panels, `=` calculator, `>` commands and actions.
Without a prefix, arithmetic shows a Calc row at the top and actions appear once something is typed.
Matching is fuzzy (a subsequence; contiguous runs and word starts score higher), apps keep v0's
name/keyword/generic-name scoring.

**Keys:** Up/Down or Ctrl+J/K select, PageUp/PageDown jump, Enter runs, Esc closes. A panel row also has
keyboard actions, shown as chips above the footer:

| Key | On the selected panel |
| --- | --- |
| Enter | focus it (`panel.focus`) |
| Ctrl+P | pet ↔ pin (`panel.toggle_pet`) |
| Ctrl+H | dock it to the HUD (`panel.place {"pin": "hud"}`, an assumption until ghostty documents the HUD pin) |
| Ctrl+M | minimize (`panel.minimize`) |
| Ctrl+W | close (`panel.close`) |
| Alt+Left / Alt+Right | move it left / right in ghostty's panel order (`panel.order`) |
| Alt+Home / Alt+End | move it first / last |

Tab and Shift+Tab cycle through the chips and Enter runs the highlighted one, so everything works without a
mouse. These actions keep the palette open (focus closes it). Without `panel.list` (an older ghostty),
window rows get the same keys: close, pin/pet and HUD fall back to `window.*`; minimize and reorder report
that they need the panel IPC. The panel methods are built against their documented shapes and tested with
JSON fixtures; ghostty's side was still in progress when they were written. The palette opens centred in the upper part of the screen, or in the other half when the mouse
would be over it, and takes keyboard focus. It closes when it loses focus (Settings → General). Its style
(rounded, translucent, one violet accent) is pushed only around this window.

**Cursor.** XivDesktop keeps the game's own cursor: while the pointer is over any XivDesktop window
(palette, app grid, settings) or one of its Umbra popups, it sets ImGui's `NoMouseCursorChange` so Dalamud
leaves the cursor alone, and clears it again when the pointer leaves (only if XivDesktop set it). It never
sets an ImGui cursor itself. Unverified in game.

The **target panel** for actions is the panel ghostty reports as focused, else the last one focused
through XivDesktop, else the newest one on the current workspace.

The calculator is a small recursive-descent parser written for this: numbers, `+ - * / % ^ **`,
parentheses, unary minus, `pi e tau`, and `sqrt abs round floor ceil sin cos tan asin acos atan ln log
log2 exp min max`. Nothing else is evaluated; input is capped at 256 characters and 32 levels of nesting.

## Key chords

All configurable in Settings → **Keybinds** (type a chord such as `Super+Shift+3`; empty unbinds).

| Default | Action |
| --- | --- |
| Super+D | toggle the launcher palette |
| Super+Enter | a new world terminal with `terminal.new` (profile and pin in Settings → General; default a pet); an older ghostty gets the *terminal line* (`new`: a dropdown tab) |
| Super+Q | close the target window panel |
| Super+Space | target panel: `window.toggle_pet`, a pet is pinned where it was last shown, anything else becomes a pet |
| Super+Shift+Space | pin the target panel in front of you (`here`) |
| Super+Left / Super+Right | `focus.cycle`: previous / next shown world panel (windows and terminals; hidden ones skipped) |
| Super+1 … Super+9 | switch workspace |
| Super+Shift+1 … Super+Shift+9 | move the target panel to that workspace |

- Chords match exactly (Super+1 does not fire on Super+Shift+1), fire once per press, and never while an
  ImGui text field has the keyboard. A consumed key is cleared in Dalamud's `IKeyState` so the game does
  not also act on it.
- Keys are read from the game's key state (`IKeyState`), the usual Dalamud way. Keys the game does not
  track, which may include VK_LWIN/VK_RWIN, are read with `GetAsyncKeyState`, only while the game window is
  in the foreground. *Read keys globally* uses `GetAsyncKeyState` for every key, so chords also work while a
  window panel has the keyboard. Whether Wine delivers the Windows keys at all is **unverified**.
- **While a panel has the keyboard** (after a click, `window.focus` or Super+Left/Right), ghostty sets
  ImGui's `WantTextInput` every frame, so Dalamud keeps key messages from the game and `IKeyState` sees
  nothing: chords do not fire. With *Read keys globally* on, XivDesktop reads them with `GetAsyncKeyState`
  and lets them through while `focus.get` reports a focused panel; the keys also reach the app in the panel.
  Esc twice gives the keyboard back to the game.
- **Super under a Linux desktop.** The desktop can take Super before Wine sees it: sway binds `$mod+d`
  itself, and GNOME opens the overview on Super. Either unbind those for the game's window, or pick another
  modifier in Settings → Keybinds → *Modifier* and **Reset all to this modifier**: *Alt+Shift* (Shift
  variants become Ctrl+Alt+Shift) or *Ctrl+Alt* (Shift variants become Ctrl+Alt+Shift). The game itself
  binds some Alt and Ctrl chords; check the game's keybind settings for clashes.
- On an older ghostty-dalamud without the v1.1 methods (XivDesktop probes with `focus.get`),
  Super+Space falls back to `window.place` with `here` / `pet`, Super+Left/Right to XivDesktop's own cycle
  over this workspace's window panels, Super+Enter to the terminal line, and hiding to `/term pin hide`.

## Workspaces

Nine numbered workspaces, sway style. Every window panel belongs to one; a new panel joins the current
workspace. Switching hides the other workspaces' panels with `window.hide {"id": N, "hidden": true}` and
shows the current ones with `"hidden": false`: a hidden panel keeps its place and size but is neither drawn
nor streamed. (An older ghostty-dalamud gets `window.place {"pin": "hide"}` / `"hide off"` instead.) Focusing a panel on another workspace (palette, taskbar) switches there first.

Membership is saved in `pluginConfigs/XivDesktop.json` by panel id, title and app. Panel ids do not survive
a ghostty reload or an agent reconnect, so a panel that comes back is re-bound best-effort: same title,
else same app with the same "… - App" title stem. Turning off *Hide other workspaces' panels* keeps the
grouping (palette, taskbar) and shows everything.

`window.list` reports each panel's `hidden` state, and XivDesktop re-sends a hide or show whenever the
report disagrees with the current workspace. Full-screen and tab views are left alone.

## Notifications

When a window panel becomes live, or ends, XivDesktop shows a Dalamud notification with the app's icon
(PNG from the catalog; a glyph when unknown). Both are toggles in Settings → General. A `window.open` that
ghostty refuses (agent not connected, too old, no player) always shows a warning.

## Umbra widgets

`tools/install-umbra.sh` builds `Umbra.XivDesktop.dll`, which holds both widgets. With `--install` it also copies it to
`~/umbra-plugins/`. In Umbra Settings → **Plugins**, accept the third-party plugin warning, **Install from
file** → pick the DLL's `Z:\` path, restart Umbra, then add the **Apps** toolbar widget. Clicking the button
opens a compact launcher: search, favourites and recents. Right-click an app to toggle it as a favourite.
Right-click the widget itself to open the launcher palette. The widget uses only the IPC below.

The **Windows** widget is a taskbar: the current workspace number, then a strip of the open window panels
(a letter tile and the title; the focused one outlined). Click a panel to focus it, middle-click to close it,
right-click for *Pin where it is / Make pet*, *Pin in front*, *Move to workspace N* and *Close*. Clicking the
workspace number lists every panel and workspace. Its settings: title length, and whether panels on other
workspaces are listed (dimmed). Umbra's `MenuPopup` is sealed and a widget cannot raise Umbra's open-popup
event itself, so the right-click menu is opened through that event's backing field by reflection.

**Neither widget has been loaded in Umbra yet**: their layout, the stylesheet, the embedded search box and
the reflection above are unverified.

## What gets listed

**First choice: ghostty-agent's list.** When ghostty-dalamud answers `agent.apps` with a non-empty list,
that list is the catalog. The agent runs on the host and reads every .desktop file there; XIVLauncher as a
Flatpak shows the game only its sandbox, where the `Z:\` scan below found 17 of 85 apps on one host. Agent
apps start with `window.open {"match": "app:<id>"}` (the agent starts its own entry); their icons are the
PNG paths the agent sends (rendered under `$HOME`, which the sandbox can read), else an icon-theme lookup,
else the scan's icon, else a letter tile. Ids get a `.desktop` suffix so favourites and recents carry over.
The list is re-read whenever the agent sends a new window list; while it is empty XivDesktop asks for one
at most every 30 s. The directory scan below stays as the fallback, and Settings → Windows shows which one
is in use.

**Fallback: the directory scan.**

- **Directories**, in increasing priority (a later directory replaces an earlier entry with the same
  desktop-file id): `/usr/share/applications`, `/usr/local/share/applications`,
  `$HOME/.local/share/applications`, `/var/lib/flatpak/exports/share/applications`,
  `$HOME/.local/share/flatpak/exports/share/applications`. Subdirectories are scanned, and `kde4/foo.desktop`
  gets the id `kde4-foo.desktop`, as the spec says.
- **$HOME under Wine:** `$HOME` if Wine passes it through as a Linux path, otherwise `WINEHOMEDIR`,
  otherwise the part of the plugin's config path before `/.xlcore`. `HomeOverride` in the config file wins
  over all three.
- **Hidden:** `Type` other than `Application`, `NoDisplay=true`, `Hidden=true` (which also masks the same
  id from an earlier directory), a `TryExec` that is not found in the usual Linux `bin` directories, an
  `Exec` with unbalanced quotes, `NotShowIn` naming `XivDesktop`, and `OnlyShowIn` naming one desktop
  family only (`GNOME;Unity;`, `KDE;`). Apps launched here run in a bare compositor, where GNOME Settings,
  GNOME Help or Extension Manager do not work. An `OnlyShowIn` that lists several families
  (`GNOME;Unity;XFCE;KDE;`) is treated as an ordinary app and shown.
- **Names:** the unlocalized `Name`/`GenericName`/`Comment`/`Keywords` win. A file with only localized keys
  falls back to `C`, then English, then the first one it finds.
- **Exec:** parsed with the spec's quoting rules. Field codes (`%f %F %u %U %i %c %k`, and the deprecated
  ones) are removed, and so are flatpak's empty `@@u … @@` file-forwarding markers. Each argument is then
  re-quoted for `sh`. Anything outside `[A-Za-z0-9_-./=:,+@%]` is single-quoted, so the command line
  ghostty-agent hands to `sh -c` has no unintended expansion.
- **Terminal=true** apps start in a new world terminal: `terminal.new` (profile and pin from Settings →
  General), and once its panel id comes back in `window.list`'s `requests`, 1.5 s later,
  `/term send #<id> <command>` types the command and Enter into it. The delay is a guess at how long the
  agent takes to start the shell (unverified). Without `terminal.new` (older ghostty-dalamud, or only the
  Post gate) they are still refused.
- **Icons:** an absolute `Icon=` path, then the `hicolor`, `Adwaita`, `AdwaitaLegacy` and `breeze` themes
  at 256, 128, 96, 64 and 48 px (then 512 px) under `/usr/share/icons`, `$HOME/.local/share/icons` and the
  flatpak export dirs, then `/usr/share/pixmaps`. Only PNG is loaded. SVG-only icons, and icons not found
  at all, are drawn as a coloured letter tile. There is no SVG renderer, and many current GNOME apps ship
  only SVG, so expect a lot of letter tiles.
- **Search:** each word must match the name, a keyword, the generic name or the id. Matches rank as exact
  > prefix > word-boundary substring > substring > subsequence (`txed` finds Text Editor). Favourites and
  recents get a small boost.

To see what your host would list, without the game:

```sh
~/.dotnet/dotnet run --project tools/scan            # counts + 10 sample apps with command and icon
~/.dotnet/dotnet run --project tools/scan -- --all --missing
```

## IPC (`XivDesktop.v1.*`)

For other plugins, the Umbra widget, and later MCP tools. Payloads are JSON strings (camelCase). Names and
shapes are in [src/Shared/IpcContract.cs](src/Shared/IpcContract.cs).

| Gate | Type | Returns |
| --- | --- | --- |
| `XivDesktop.v1.ListApps` | `Func<string>` | `[{ id, name, generic, categories, favourite, keywords, recent, terminal, launchable }]` |
| `XivDesktop.v1.Launch` | `Func<string, string>` | `"ok: launched NAME (ID)"` or `"error: REASON"`; takes an id or a search |
| `XivDesktop.v1.Status` | `Func<string>` | `{ ghostty, ghosttyStatus, apps, scanning, scannedAt, lastLaunch, lastError, summary }` |
| `XivDesktop.v1.ToggleLauncher` | `Action<string>` | toggles the window; the argument pre-fills the search |
| `XivDesktop.v1.ToggleFavourite` | `Func<string, bool>` | new favourite state |
| `XivDesktop.v1.Windows` | `Func<string>` | window panels with workspaces, see below |
| `XivDesktop.v1.Workspace` | `Func<int, string>` | `1..9` switches; `0` only reports; `"ok: workspace N"` or `"error: REASON"` |
| `XivDesktop.v1.Palette` | `Func<string, string>` | the palette's top 10 rows for a query, see below |
| `XivDesktop.v1.WindowAction` | `Func<string, string>` | `"ok: …"` or `"error: …"`; takes the JSON below |

`ok` from `Launch` means the command was handed to ghostty-dalamud, not that a window appeared. `ok` from
`WindowAction` and `Workspace` means ghostty queued the change; the outcome shows in the next `Windows`.
`Windows` is rebuilt on the framework thread after every change and is free to read from any thread;
`Workspace`, `Palette` and `WindowAction` run on the framework thread (inline when called from it) and give
up after two seconds.

These are the gates meant for xiv-mcp's tools (`list_windows`, `switch_workspace`, `palette_search`,
`window_action`); none exist there yet.

`XivDesktop.v1.Windows`:

```json
{
  "available": true,
  "rev": 7,
  "workspace": 1,
  "target": 12,
  "windows": [
    {"id": 12, "title": "Yad Window", "app": "yad", "state": "live", "kind": "pet",
     "focused": false, "workspace": 1, "hidden": false, "appId": "yad-calendar.desktop"}
  ]
}
```

`available`: ghostty-dalamud's Call gate is registered (else `windows` is empty). `rev`: ghostty's
`window.list` rev, `-1` before the first answer. `target`: the panel actions without an id apply to.
`state`, `kind`: as ghostty reports them (`pending|live|ended`, `pet|pin|full|tab`). `workspace`: `1..9`,
`0` while not yet assigned. `hidden`: XivDesktop hid it. `appId`: best-guess desktop-file id, or `null`.

`XivDesktop.v1.Palette("fire")` (scores illustrative):

```json
[
  {"provider": "app", "title": "Firefox", "subtitle": "Web Browser", "score": 168.4, "enabled": true,
   "reason": null, "command": {"kind": "launch", "arg": "org.mozilla.firefox.desktop", "id": 0, "number": 0},
   "appId": "org.mozilla.firefox.desktop", "windowId": null},
  {"provider": "window", "title": "Mozilla Firefox", "subtitle": "firefox · pet · workspace 1",
   "score": 131.5, "enabled": true, "reason": null,
   "command": {"kind": "focus", "arg": "", "id": 12, "number": 0}, "appId": null, "windowId": 12}
]
```

`provider`: `app|window|action|calc|command`. `command.kind`: `launch` (arg: app id), `focus`, `close`,
`place` (arg: pin arguments), `workspace` (number), `move` (id, number), `reload`, `copy` (arg: text),
`post` (arg: a `/term` line), `chat` (arg: a slash command), `grid`. The prefixes (`a:`, `w:`, `=`, `>`)
work here too. Palette only ranks; to act, use `Launch`, `WindowAction` or `Workspace`.

`XivDesktop.v1.WindowAction` takes:

```json
{"action": "focus|close|pet|pin|toggle|place|move", "id": 12, "pin": "orbit 3.5", "workspace": 3}
```

`id` 0 or missing means the target panel. `pin` is only for `place` (as `/term pin` takes it); `pin` is
`place` with `here`; `toggle` is `window.toggle_pet`; `workspace` is only for `move`.

## Development

```sh
~/.dotnet/dotnet build            # everything; outputs go to ~/xiv-desktop-build/artifacts
~/.dotnet/dotnet test             # host tests: catalog, search, IPC payloads, palette, calculator, chords,
                                  # workspaces, ghostty window.list parsing, agent apps (188 tests)
```

Tests build throwaway directory trees for icon and override cases. They never touch Dalamud or the game.

## Not implemented yet

Clipboard, file drops, saved layouts, MCP tools in xiv-mcp (the gates above are ready for them), window
placement per zone, app icons in the Umbra taskbar (letter tiles for now), recording a chord by pressing it.
See [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md#later) for what each needs from ghostty-dalamud.

## Desktop integrations (planned)

The direction is a game session that is also the desktop session: sway today, GNOME later. **None of this
is implemented.**

- **GNOME:** a GNOME Shell extension that talks to XivDesktop through ghostty-agent (not through Wine):
  - *Send to game*: a window-menu entry and a keybind that hands a running app to the game, i.e.
    `window.open {"match": …}` on its window. Moving an already-mapped GNOME window into the agent's
    compositor is not possible as such; the realistic route is ghostty-agent capturing it through the
    desktop portal (screencast) the way it captures on Windows.
  - *Notifications mirroring*: freedesktop notifications on the host shown as Dalamud notifications, and
    the other way round for in-game events.
  - *Clipboard*: host clipboard ↔ Wine clipboard, through the agent.
  - *GSConnect-like*: a phone-style bridge (notifications, clipboard, "open on the other side") between the
    Linux desktop and the game session.
- **KDE Plasma / SteamOS:** later; the same agent-side pieces with a KWin script instead of a Shell
  extension. Not planned for now.
