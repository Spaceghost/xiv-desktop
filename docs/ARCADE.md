# Arcade: your own classic games as game panels, saves kept in step

`/arcade` lists the classic games **you** put in a folder, starts them in RetroArch on your Linux
desktop so they appear as a panel in the game (pin it, pet it, full-screen it like any other panel),
and keeps every battery save and save state in one folder that Syncthing copies between your machines.

**Status: beta.** The host helper and the plugin's logic are covered by tests. None of it has been seen
working in the game, with a real emulator, or syncing between two real machines. See
[What is verified](#what-is-verified-and-what-is-not).

## Bring your own games

XivDesktop never downloads, links to or helps find ROMs, disc images or BIOS files. It only reads
folders you point it at. Use your own dumps of games you own. The same goes for artwork: a picture is
shown only when the file is already on your disk; nothing is scraped. PlayStation and PlayStation 2
cores want a BIOS dumped from your own console; XivDesktop will not look for one.

## What you type

| Command | What it does |
| --- | --- |
| `/arcade` | Opens the Arcade window. With no games it is the three-step welcome. |
| `/arcade <name>` | Plays the closest match: `/arcade ff7`, `/arcade tactics`, `/arcade x-2`. |
| `/arcade last` | Resumes the most recent game. |
| `/arcade list` | Prints the library by console. |
| `/arcade sync` | Forces a save sync and prints its status. |
| `/arcade setup` | The first-run checklist, in the window and in chat. |
| `/arcade rescan` | Looks at the folders again (the window does this by itself while open). |
| `/arcade launchbox <folder>` | Reads a LaunchBox folder where it lives. |
| `/arcade import-saves <folder>` | Copies saves from another RetroArch install. Never over a newer save. |
| `/arcade launch --dry-run <name or file>` | Prints the exact emulator command and starts nothing. |

The launcher palette (Super+D) finds games by name once you type, with an `Arcade` badge.
In the window: type to search, arrows move, Enter plays, F5 rescans, F6 syncs saves, Esc closes.

## First run: three steps

Opening `/arcade` creates everything that can be created for you and shows three checks that turn
green by themselves:

1. **Choose your games folder.** The default, `~/Games/Arcade/`, is made for you with one folder per
   console and a `README.txt` in each naming the accepted file types. Another place:
   `xiv-arcade setup --games /some/where`.
2. **Install the emulator.** One line, shown with a Copy button. It installs RetroArch from Flathub and
   lets its sandbox reach the games folder and the saves folder. Nothing is installed for you.
   Cores are RetroArch's own download: *Main Menu > Online Updater > Core Downloader*. The window
   names the exact core for each console you have games for.
3. **Drop your own game files in.** Copy them into the console folders. They appear in the window
   without a restart.

Below the steps is the Final Fantasy shelf: titles, consoles and years as plain text. An entry
lights up when a matching file of yours appears. Anything else lands under "Everything else", by console.

### Folders and consoles

| Folder | Console | Files | Core (first found wins) |
| --- | --- | --- | --- |
| `nes` | NES / Famicom | `.nes .fds .unf` | mesen, nestopia, fceumm |
| `snes` | Super NES / Super Famicom | `.sfc .smc` | snes9x, bsnes |
| `gb`, `gbc` | Game Boy, Game Boy Color | `.gb`, `.gbc` | gambatte, sameboy, mgba |
| `gba` | Game Boy Advance | `.gba` | mgba, vba_next |
| `nds` | Nintendo DS | `.nds` | melondsds, melonds, desmume |
| `wonderswan` | WonderSwan | `.ws .wsc` | mednafen_wswan |
| `psx` | PlayStation | `.cue .chd .pbp .m3u .iso` | swanstation, mednafen_psx_hw, mednafen_psx, pcsx_rearmed |
| `ps2` | PlayStation 2 | `.iso .chd .cso` | pcsx2 (LRPS2). Standalone PCSX2 is not wired up. |
| `psp` | PlayStation Portable | `.iso .cso .pbp` | ppsspp |
| `genesis`, `n64` | Mega Drive, Nintendo 64 | `.md .gen`, `.z64 .n64` | genesis_plus_gx, mupen64plus_next |

The console comes from the folder name, then from an extension only one console uses. An `.iso` or
`.chd` outside a console folder is ambiguous and is left out until you put it in one.

Multi-disc games named `Game (Disc 1).chd`, `Game (Disc 2).chd` become one entry. The playlist
(`.m3u`) is written to `~/.config/xiv-arcade/playlists/`, never into your games folder. Your own `.m3u`
is used as it is. On the Super NES, "Final Fantasy II" and "III" light up IV and VI.

Artwork is optional: an image with the game's name (`.png`, `.jpg`) beside it or in a `boxart/` folder.

## Coming from LaunchBox

Copy your LaunchBox folder, or only its `Games`, `Images` and `Data` folders, into `~/Games/Arcade/`
as they are. Or leave it where it is and point at it: `/arcade launchbox /path/to/LaunchBox` (the
welcome screen has the same one-step box). What is read:

- **`Games/<Platform Name>/`** with LaunchBox's platform names, in any case: "Nintendo Entertainment
  System", "Super Nintendo Entertainment System", "Nintendo Game Boy", "Nintendo Game Boy Color",
  "Nintendo Game Boy Advance", "Nintendo DS", "Sony Playstation", "Sony Playstation 2", "Sony PSP",
  "Sega Genesis", "Nintendo 64", "WonderSwan Color".
- **`Data/Platforms/<Platform>.xml`** when present: titles, sort titles, release years, and each
  game's `ApplicationPath`. Windows paths are normalised: backslashes, drive letters and absolute
  paths such as `D:\LaunchBox\Games\...` are matched to the longest tail that exists under the copied
  folder, ignoring case. Discs listed as `AdditionalApplication` entries with a `Disc` number become
  one `.m3u`. A game whose file was not copied is simply absent.
- **`Images/<Platform>/Box - Front/`**, then `Box - 3D`, `Clear Logo`, `Screenshot - Game Title`,
  `Screenshot - Gameplay`, including region subfolders. LaunchBox's filename sanitising (`:` and `'`
  become `_`) and its `-01`, `-02` suffixes are tolerated; the lowest number wins.

A game is its console plus its title, so the same game in the LaunchBox tree and in your plain
`<console>/` folders is one entry. `Emulators/`, `Metadata/` and the like are skipped.

Saves from a Windows RetroArch (for example `LaunchBox\Emulators\RetroArch`):
`/arcade import-saves /path/to/RetroArch`. It reads `saves/` and `states/` with their per-core
subfolders. A save named after a game in your library goes to that game's console; otherwise the core
folder decides (Snes9x, mGBA, SwanStation, PPSSPP...). A save that is already here and newer, or the
same age, is left alone. Files it cannot place are listed, not guessed.

## How launching works

The plugin hands one shell line to the Ghostty launch backend, which starts it inside the host agent's
headless compositor; the emulator's window is then a game panel. The line runs the helper, and the helper:

1. asks Syncthing to rescan and waits up to 30 s for incoming saves; **if saves are still arriving it
   refuses to start the game** and says so,
2. resolves any conflict (below) and copies the game's current saves to
   `~/.local/share/xiv-arcade/backups/<game>/<time>/` (last 10 kept, not synced),
3. runs `flatpak run org.libretro.RetroArch -L <core>.so --appendconfig <console>.cfg <your file>`,
4. when the emulator closes, records the play, asks for another rescan so the new save goes out, and
   writes the result where the window reads it.

Your own `retroarch.cfg` is never edited. The appended config points `savefile_directory` and
`savestate_directory` at the synced tree, turns off RetroArch's per-core sorting so the path is the
same on every machine whatever core runs the game, and saves battery RAM every 10 s.

### Controller

Not verified. The agent's compositor forwards keyboard and pointer to a focused panel. A gamepad does
not go through it: RetroArch reads the pad from the host directly, and FFXIV reads the same pad. So
while you play in a panel, **FFXIV will also react to the controller**. `pause_nonactive` is set, but
whether RetroArch sees focus inside the headless compositor is untested. Until this is solved, play
with the keyboard, or expect your character to move. This is a known limitation, not a setting you missed.

## Save sync

Every save and state lives in one tree:

```
~/.local/share/xiv-arcade/sync/
  saves/<console>/     battery saves, memory cards
  states/<console>/    save states
  .stignore            temp and editor files never travel
```

Syncthing copies that folder between your machines, peer to peer; over Tailscale it needs no hub and
no open ports. The generated folder config turns **file versioning on** (staggered, one year) and sets
`maxConflicts` to unlimited, so a replaced or conflicting save is never destroyed.

XivDesktop does not install or start Syncthing, enable a service, or pair a device. It prints the
commands. The minimum, on each machine:

```sh
# 1. Syncthing itself (pick what fits the machine), running as your user
brew install syncthing && brew services start syncthing     # or your distribution's package, then:
systemctl --user enable --now syncthing.service

# 2. The Arcade saves folder, with versioning on (prints the commands; --apply runs them)
python3 ~/.config/xiv-arcade/xiv-arcade sync-setup --apply

# 3. Pair: print this machine's ID, then on the OTHER machine add it
python3 ~/.config/xiv-arcade/xiv-arcade pair
python3 ~/.config/xiv-arcade/xiv-arcade pair DEVICE-ID --name other-pc --apply
#    to keep traffic on your tailnet add:  --address tcp://<tailscale-name>:22000
```

Do step 3 in both directions, or accept the prompt in Syncthing's web page on the second machine.
`sync-setup` also prints the `<folder>` XML to paste into `config.xml` if you prefer that. The helper is
the plugin's own copy; `tools/xiv-arcade` in the repository is the same file and runs without the game.
The exact `syncthing cli` spellings have not been run against a real Syncthing; if one is refused,
nothing further runs, and the XML or the web page does the same job.

### What the status means

| Status | Meaning | Can you launch? |
| --- | --- | --- |
| In sync / Ready · nothing to sync yet | Up to date with every connected device. | yes |
| Ready · no other device yet | Folder set up, nothing paired. | yes |
| Sending saves… | This machine has the newest saves and is pushing them. | yes |
| **Syncing…** | Newer saves are still arriving. | **no**, until they are complete |
| Other device offline | Saves go out when it returns. If it also played, both saves are kept. | yes |
| Conflict: both saves kept | See below. | yes |
| Sync is off / not set up / paused / error | Saves stay on this machine. | yes |

### Conflicts

If the same save changed on two machines, Syncthing keeps the losing copy as
`name.sync-conflict-DATE-TIME-DEVICE.ext`. The helper then makes the **newer** file (by modification
time) the live save and keeps the other beside it as
`name (older save, device ABCDEFG, 2026-09-20 14.03).srm`. Nothing is deleted. The window and chat tell
you, and the conflict is recorded. To go back, swap the two files' names. Older versions are also in
Syncthing's `.stversions/` and in the pre-launch backups.

## Where things live

| Path | What |
| --- | --- |
| `~/Games/Arcade/` | Your games (default). Not synced. |
| `~/.local/share/xiv-arcade/sync/` | Saves and states. The only synced tree. |
| `~/.local/share/xiv-arcade/backups/` | Pre-launch save copies. Local. |
| `~/.config/xiv-arcade/arcade.db` | SQLite: library index, play history, sync state, conflicts. |
| `~/.config/xiv-arcade/state.json` | What the window reads. Rewritten by every helper command. |
| `~/.config/xiv-arcade/config.json` | Games folder, extra folders. Your device names stay here, never in a repository. |
| `~/.config/xiv-arcade/xiv-arcade` | The helper, written by the plugin from its own build. |

The helper follows `XDG_CONFIG_HOME` and `XDG_DATA_HOME`; the plugin assumes the defaults.

## What is verified and what is not

Verified by tests (`python3 -B -m unittest discover -s tests/arcade`, and the C# suite), using empty
placeholder files only: console detection from paths, LaunchBox platform names, XML parsing, Windows
path normalisation, image lookup, multi-disc playlists, fuzzy matching, the emulator command per core
(also via `--dry-run`), the sync state machine (newer wins, conflict keeps both, offline peer, refusal
while saves arrive), save import never overwriting a newer save, the SQLite index, the first-run state
with no games, shell quoting of names and paths, and the palette rows.

**Not verified:** anything in the game (the window, the panel, focus, the controller), any real
emulator or core, RetroArch honouring the appended config inside Flatpak, the `syncthing cli` commands,
and sync between two real machines. No game was launched: no test ROM was used, so launching is covered
by command construction and `--dry-run` only.
