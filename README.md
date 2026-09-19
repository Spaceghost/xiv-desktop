# xiv-desktop

**XivDesktop** is a Dalamud plugin that lists the Linux host's desktop applications inside FINAL FANTASY
XIV and opens them as windows in the game world. It does not draw or stream any windows itself. It sits on
top of ghostty-dalamud: that plugin's `ghostty-agent` runs a
headless Wayland compositor on the host and shows its windows as world panels. XivDesktop adds the parts a
desktop has and a terminal command does not: an app catalog with icons, search, favourites and recents, a
`/desktop` launcher, IPC for other plugins, and an optional Umbra toolbar button.

> **Status (v0).** The solution builds against Dalamud API 15 (.NET 10). The catalog library has host-side
> tests, and the scanner has been run against a real Bazzite host's application directories (Linux paths,
> not through Wine). **Nothing has been observed in the game yet.** The launcher window, the icon textures,
> the IPC gates, the Umbra widget, and whether `Z:\` reads behave under Wine as they do on the host are all
> unverified until someone loads the plugin. Treat everything below about in-game behaviour as the design,
> not as a result.

## How it fits together

```
/desktop window ─┐
Umbra "Apps" ────┼─ XivDesktop.v1.* IPC ─> XivDesktop plugin (in game, under Wine)
other plugins ───┘                           ├─ catalog: reads Z:\usr\share\applications ... off the framework thread
                                             ├─ icons: Z:\...\hicolor\256x256\apps\*.png -> ITextureProvider
                                             └─ launch: GhosttyDalamud.v1.Post("window pull run <cmd>")
                                                          │
ghostty-dalamud (in game) ── /term window pull run ───────┘
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
| `/desktop` | toggle the launcher window |
| `/desktop <text>` | open it with a search already typed |
| `/desktop launch <id or search>` | launch an app by desktop-file id (`org.gnome.TextEditor`), exact name, or best search match |
| `/desktop reload` | rescan the application directories (runs off the framework thread) |
| `/desktop status` | one line: app count, and whether ghostty-dalamud is there |

The launcher has a search box (focused when the window opens), the favourites and recent rows (shown while
the search is empty), and an icon grid. **Up/Down** move by row. **Left/Right** move by one while the search
box is empty or unfocused. **Enter** launches the selection. Clicking a cell launches it. Right-click gives
*Add to favourites*, *Launch* and *Copy command*. The window closes after a launch. Favourites and recents
are stored in `pluginConfigs/XivDesktop.json`.

A launch posts `window pull run <command>` to ghostty-dalamud, the same as typing
`/term window pull run <command>`. The new window appears as a pet beside the character.

## Umbra widget

`tools/install-umbra.sh` builds `Umbra.XivDesktop.dll`. With `--install` it also copies it to
`~/umbra-plugins/`. In Umbra Settings → **Plugins**, accept the third-party plugin warning, **Install from
file** → pick the DLL's `Z:\` path, restart Umbra, then add the **Apps** toolbar widget. Clicking the button
opens a compact launcher: search, favourites and recents. Right-click an app to toggle it as a favourite.
Right-click the widget itself to open the full `/desktop` window. The widget uses only the IPC below.
**It has not been loaded in Umbra yet**, so its layout and the embedded search box are unverified.

## What gets listed

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
- **Terminal=true** apps are listed (dimmed) but **not launched** in v0. `window pull run` starts a
  Wayland client with no terminal attached, so a TUI would run invisibly. Opening a ghostty tab (`new`) and
  typing the command into it (`send`) would work in principle. The two posts are not ordered against the
  new tab becoming active, though, so v0 refuses with an explanation instead.
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

`ok` from `Launch` means the command was handed to ghostty-dalamud, not that a window appeared.

## Development

```sh
~/.dotnet/dotnet build            # everything; outputs go to ~/xiv-desktop-build/artifacts
~/.dotnet/dotnet test             # host tests (parser, Exec, overrides, visibility, icons, search, IPC payloads)
```

Tests build throwaway directory trees for icon and override cases. They never touch Dalamud or the game.

## Not implemented yet

Taskbar of open windows, notifications, clipboard, file drops, saved layouts, MCP tools, window placement
per zone. See [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md#later) for what each needs from ghostty-dalamud.
