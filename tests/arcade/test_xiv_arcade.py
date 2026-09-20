"""Host tests for tools/xiv-arcade. Every game file here is an empty placeholder made by the test;
no real game, BIOS or artwork is used or fetched. Run: python3 -B -m unittest discover -s tests/arcade"""
import importlib.machinery
import importlib.util
import json
import os
import sys
import tempfile
import unittest
from pathlib import Path

_PATH = Path(__file__).resolve().parents[2] / "tools" / "xiv-arcade"
_loader = importlib.machinery.SourceFileLoader("xiv_arcade", str(_PATH))
_spec = importlib.util.spec_from_loader("xiv_arcade", _loader)
xa = importlib.util.module_from_spec(_spec)
sys.modules["xiv_arcade"] = xa
sys.dont_write_bytecode = True
_loader.exec_module(xa)


class Tree(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.home = Path(self.tmp.name) / "home"
        self.env = xa.Env(self.home, config=self.home / ".config/xiv-arcade", data=self.home / ".local/share/xiv-arcade",
                          which=lambda name: None, flatpak_roots=(self.home / "flatpak/app",))
        self.games = self.env.games_root()

    def tearDown(self):
        self.tmp.cleanup()

    def touch(self, path, text="", mtime=None):
        p = Path(path)
        p.parent.mkdir(parents=True, exist_ok=True)
        p.write_text(text)
        if mtime is not None:
            os.utime(p, (mtime, mtime))
        return p

    def install_retroarch(self, *cores):
        (self.home / "flatpak/app" / xa.RETROARCH_FLATPAK).mkdir(parents=True)
        for c in cores:
            self.touch(self.home / ".var/app" / xa.RETROARCH_FLATPAK / "config/retroarch/cores" / f"{c}_libretro.so")


class SystemDetection(Tree):
    def test_folder_names_short_and_launchbox(self):
        for folder, ext, want in [("snes", ".sfc", "snes"), ("PSX", ".chd", "psx"), ("ps2", ".iso", "ps2"), ("psp", ".iso", "psp"),
                                  ("Sony Playstation", ".chd", "psx"), ("sony playstation 2", ".iso", "ps2"),
                                  ("Sony PSP", ".cso", "psp"), ("Nintendo Entertainment System", ".nes", "nes"),
                                  ("Super Nintendo Entertainment System", ".smc", "snes"),
                                  ("Nintendo Game Boy Advance", ".gba", "gba"), ("Nintendo DS", ".nds", "nds"),
                                  ("Nintendo Game Boy", ".gb", "gb")]:
            got = xa.detect_system(f"/g/{folder}/Some Game{ext}", "/g")
            self.assertEqual(want, got.id if got else None, folder)

    def test_unique_extension_without_a_folder(self):
        self.assertEqual("gba", xa.detect_system("/g/loose/x.gba", "/g").id)
        self.assertEqual("psx", xa.detect_system("/g/x.cue", "/g").id)

    def test_ambiguous_image_outside_a_system_folder_is_unknown(self):
        self.assertIsNone(xa.detect_system("/g/x.iso", "/g"))
        self.assertIsNone(xa.detect_system("/g/x.chd", "/g"))

    def test_wrong_extension_in_a_system_folder_is_not_a_game(self):
        self.assertIsNone(xa.detect_system("/g/snes/readme.txt", "/g"))
        self.assertIsNone(xa.detect_system("/g/snes/x.gba", "/g"))

    def test_nearest_folder_wins(self):
        self.assertEqual("psp", xa.detect_system("/g/Sony Playstation/psp/x.iso", "/g").id)


class TitlesAndDiscs(Tree):
    def test_clean_title(self):
        self.assertEqual("Final Fantasy VII", xa.clean_title("Final Fantasy VII (USA) (Disc 1)"))
        self.assertEqual("The Final Fantasy Legend", xa.clean_title("Final Fantasy Legend, The (USA)"))
        self.assertEqual("Final Fantasy IV", xa.clean_title("Final_Fantasy_IV [!]"))

    def test_m3u_generated_in_disc_order_and_discs_become_one_entry(self):
        for n in (2, 1, 3):
            self.touch(self.games / "psx" / f"Final Fantasy VII (USA) (Disc {n}).chd")
        self.touch(self.games / "psx" / "Final Fantasy Tactics (USA).chd")
        games = xa.scan(self.env)
        self.assertEqual(["Final Fantasy Tactics", "Final Fantasy VII"], [g["title"] for g in games])
        ff7 = games[1]
        self.assertTrue(ff7["path"].endswith("playlists/psx/Final Fantasy VII.m3u"))
        lines = Path(ff7["path"]).read_text().splitlines()
        self.assertEqual([f"Final Fantasy VII (USA) (Disc {n}).chd" for n in (1, 2, 3)], [Path(x).name for x in lines])
        self.assertFalse(str(ff7["path"]).startswith(str(self.games)), "the player's folder is never written to")

    def test_players_own_m3u_is_used_and_its_discs_hidden(self):
        d = self.games / "psx"
        self.touch(d / "FF8 (Disc 1).chd")
        self.touch(d / "FF8 (Disc 2).chd")
        self.touch(d / "Final Fantasy VIII.m3u", "FF8 (Disc 1).chd\nFF8 (Disc 2).chd\n")
        games = xa.scan(self.env)
        self.assertEqual(["Final Fantasy VIII"], [g["title"] for g in games])
        self.assertEqual(str(d / "Final Fantasy VIII.m3u"), games[0]["path"])

    def test_bin_tracks_beside_a_cue_are_not_games(self):
        d = self.games / "psx"
        self.touch(d / "Final Fantasy IX.cue")
        self.touch(d / "Final Fantasy IX (Track 1).bin")
        self.touch(d / "Final Fantasy IX.bin")
        self.assertEqual(["Final Fantasy IX"], [g["title"] for g in xa.scan(self.env)])

    def test_boxart_only_from_the_players_files(self):
        self.touch(self.games / "snes" / "Final Fantasy VI.sfc")
        self.assertIsNone(xa.scan(self.env)[0]["boxart"])
        art = self.touch(self.games / "snes" / "boxart" / "Final Fantasy VI.png")
        self.assertEqual(str(art), xa.scan(self.env)[0]["boxart"])


class Shelf(unittest.TestCase):
    def test_numbering_and_compilations(self):
        self.assertEqual(["ff7"], xa.ff_keys("Final Fantasy VII", "psx"))
        self.assertEqual(["ff10-2"], xa.ff_keys("Final Fantasy X-2", "ps2"))
        self.assertEqual(["ff10"], xa.ff_keys("Final Fantasy X", "ps2"))
        self.assertEqual(["ff1", "ff2"], xa.ff_keys("Final Fantasy I & II - Dawn of Souls", "gba"))
        self.assertEqual(["ff5", "ff6"], xa.ff_keys("Final Fantasy Anthology", "psx"))
        self.assertEqual(["fft"], xa.ff_keys("Final Fantasy Tactics", "psx"))
        self.assertEqual(["ffta"], xa.ff_keys("Final Fantasy Tactics Advance", "gba"))
        self.assertEqual(["ff12rw"], xa.ff_keys("Final Fantasy XII - Revenant Wings", "nds"))
        self.assertEqual(["ff4"], xa.ff_keys("Final Fantasy IV Advance", "gba"))
        self.assertEqual([], xa.ff_keys("Chrono Trigger", "snes"))

    def test_american_snes_numbers(self):
        self.assertEqual(["ff4"], xa.ff_keys("Final Fantasy II", "snes"))
        self.assertEqual(["ff6"], xa.ff_keys("Final Fantasy III", "snes"))
        self.assertEqual(["ff3"], xa.ff_keys("Final Fantasy III", "nes"))

    def test_every_entry_is_text_only(self):
        for e in xa.FF_SHELF:
            self.assertTrue(e.title and e.platforms and 1985 < e.year < 2015)


class Fuzzy(unittest.TestCase):
    GAMES = [{"title": t, "ff": ["x"]} for t in ("Final Fantasy VII", "Final Fantasy VIII", "Final Fantasy Tactics",
                                                 "Final Fantasy X", "Final Fantasy X-2", "Final Fantasy IV")] + \
            [{"title": "Chrono Cross", "ff": []}]

    def pick(self, q):
        g = xa.fuzzy_pick(self.GAMES, q)
        return g["title"] if g else None

    def test_picks(self):
        self.assertEqual("Final Fantasy VII", self.pick("ff7"))
        self.assertEqual("Final Fantasy VII", self.pick("final fantasy vii"))
        self.assertEqual("Final Fantasy VIII", self.pick("ff8"))
        self.assertEqual("Final Fantasy Tactics", self.pick("tactics"))
        self.assertEqual("Final Fantasy X-2", self.pick("x-2"))
        self.assertEqual("Final Fantasy X", self.pick("ffx"))
        self.assertEqual("Final Fantasy IV", self.pick("ff 4"))
        self.assertEqual("Chrono Cross", self.pick("chro"))
        self.assertIsNone(self.pick("zelda"))


class LaunchCommand(Tree):
    def test_per_core_commands(self):
        cores = {"nes": "mesen", "snes": "snes9x", "gb": "gambatte", "gba": "mgba", "nds": "melondsds", "psx": "swanstation",
                 "ps2": "pcsx2", "psp": "ppsspp", "wswan": "mednafen_wswan"}
        self.install_retroarch(*cores.values())
        xa.setup(self.env)
        front = xa.find_frontend(self.env)
        self.assertEqual("flatpak", front.kind)
        for sid, core in cores.items():
            system = xa.SYSTEM_BY_ID[sid]
            argv, found = xa.plan_launch(self.env, {"system": sid, "path": f"/g/{sid}/Game{system.exts[0]}"})
            self.assertEqual(["flatpak", "run", "org.libretro.RetroArch", "-L"], argv[:4])
            self.assertTrue(argv[4].endswith(f"/cores/{core}_libretro.so"), argv)
            self.assertEqual(["--appendconfig", str(self.env.config / "cfg" / f"{sid}.cfg")], argv[5:7])
            self.assertEqual(f"/g/{sid}/Game{system.exts[0]}", argv[7])
            cfg = (self.env.config / "cfg" / f"{sid}.cfg").read_text()
            self.assertIn(f'savefile_directory = "{self.env.sync_root}/saves/{sid}"', cfg)
            self.assertIn(f'savestate_directory = "{self.env.sync_root}/states/{sid}"', cfg)

    def test_preferred_core_first_then_fallback(self):
        self.install_retroarch("bsnes")
        self.assertTrue(str(xa.find_core(xa.find_frontend(self.env), xa.SYSTEM_BY_ID["snes"])).endswith("bsnes_libretro.so"))
        self.install_retroarch_core("snes9x")
        self.assertTrue(str(xa.find_core(xa.find_frontend(self.env), xa.SYSTEM_BY_ID["snes"])).endswith("snes9x_libretro.so"))

    def install_retroarch_core(self, c):
        self.touch(self.home / ".var/app" / xa.RETROARCH_FLATPAK / "config/retroarch/cores" / f"{c}_libretro.so")

    def test_nothing_installed_says_exactly_what_to_install(self):
        with self.assertRaises(xa.Refused) as e:
            xa.plan_launch(self.env, {"system": "gba", "path": "/g/gba/x.gba"})
        self.assertIn("flatpak install -y flathub org.libretro.RetroArch", str(e.exception))
        self.install_retroarch()
        with self.assertRaises(xa.Refused) as e:
            xa.plan_launch(self.env, {"system": "gba", "path": "/g/gba/x.gba"})
        self.assertIn("mgba", str(e.exception))

    def test_dry_run_prints_the_command_and_runs_nothing(self):
        self.install_retroarch("mgba")
        xa.setup(self.env)
        rom = self.touch(self.games / "gba" / "Final Fantasy Tactics Advance (USA).gba")
        index = xa.Index(":memory:")
        index.replace_library(xa.scan(self.env))
        lines, ran = [], []
        code = xa.launch(self.env, index, FakeSync(), "tactics advance", dry_run=True, run=ran.append, out=lines.append)
        self.assertEqual((0, []), (code, ran))
        self.assertIn("-L", lines[0])
        self.assertTrue(lines[0].endswith("'" + str(rom) + "'"))


class FakeSync:
    def __init__(self, *snaps):
        self.snaps = list(snaps) or [xa.SyncSnapshot(running=True, configured=True, state="idle")]
        self.rescans = 0

    def snapshot(self):
        return self.snaps.pop(0) if len(self.snaps) > 1 else self.snaps[0]

    def rescan(self):
        self.rescans += 1
        return True


def snap(**kw):
    base = dict(running=True, configured=True, state="idle", files=3)
    base.update(kw)
    return xa.SyncSnapshot(**base)


PEER_UP = {"id": "AAAAAAA", "name": "", "connected": True, "completion": 100}
PEER_DOWN = {"id": "BBBBBBB", "name": "", "connected": False, "completion": 0}


class SyncStateMachine(Tree):
    def test_verdicts(self):
        cases = [
            (xa.SyncSnapshot(), "off", True),
            (xa.SyncSnapshot(running=True), "unconfigured", True),
            (snap(peers=[]), "no-peers", True),
            (snap(peers=[PEER_DOWN]), "peer-offline", True),
            (snap(peers=[PEER_UP]), "in-sync", True),
            (snap(peers=[PEER_UP], need_bytes=4096), "pulling", False),
            (snap(peers=[PEER_UP], state="syncing"), "pulling", False),
            (snap(peers=[PEER_UP], state="scanning"), "pulling", False),
            (snap(peers=[dict(PEER_UP, completion=40)]), "pushing", True),
            (snap(peers=[PEER_UP], conflicts=1), "conflict", True),
            (snap(peers=[PEER_UP], state="error"), "error", True),
            (snap(peers=[PEER_UP], paused=True), "paused", True),
        ]
        for s, code, can in cases:
            v = xa.decide(s)
            self.assertEqual((code, can), (v.code, v.can_launch), s)

    def test_empty_tree_reads_as_ready(self):
        self.assertEqual("Ready · nothing to sync yet", xa.decide(snap(peers=[PEER_UP], files=0)).label)

    def _library(self):
        self.install_retroarch("mgba")
        xa.setup(self.env)
        self.touch(self.games / "gba" / "Final Fantasy VI Advance.gba")
        index = xa.Index(":memory:")
        index.replace_library(xa.scan(self.env))
        return index

    def test_launch_refused_while_saves_are_arriving(self):
        index, ran = self._library(), []
        sync = FakeSync(snap(peers=[PEER_UP], need_bytes=10))
        with self.assertRaises(xa.Refused):
            xa.launch(self.env, index, sync, "ff6", run=ran.append, wait=0, out=lambda s: None)
        self.assertEqual([], ran)
        self.assertEqual(0, index.db.execute("SELECT COUNT(*) FROM plays").fetchone()[0])
        self.assertIn("still syncing", json.loads(self.env.state_path.read_text())["message"])

    def test_launch_waits_for_the_pull_then_plays_and_pushes(self):
        index, ran = self._library(), []
        sync = FakeSync(snap(peers=[PEER_UP], need_bytes=10), snap(peers=[PEER_UP]))
        code = xa.launch(self.env, index, sync, "ff6", run=lambda argv: ran.append(argv) or 0, wait=5,
                         out=lambda s: None)
        self.assertEqual(0, code)
        self.assertEqual(1, len(ran))
        self.assertGreaterEqual(sync.rescans, 2, "a rescan before launch (pull) and one after exit (push)")
        self.assertEqual("Final Fantasy VI Advance", index.last_played()["title"])
        row = index.db.execute("SELECT ended, exit_code FROM plays").fetchone()
        self.assertIsNotNone(row["ended"])

    def test_offline_peer_still_launches(self):
        index, ran = self._library(), []
        xa.launch(self.env, index, FakeSync(snap(peers=[PEER_DOWN])), "ff6", run=lambda a: ran.append(a) or 0, out=lambda s: None)
        self.assertEqual(1, len(ran))

    def test_saves_are_backed_up_before_launch(self):
        index = self._library()
        save = self.touch(self.env.sync_root / "saves/gba/Final Fantasy VI Advance.srm", "v1")
        self.touch(self.env.sync_root / "saves/gba/Final Fantasy VI Advance Extra.srm", "other game")
        xa.launch(self.env, index, FakeSync(), "ff6", run=lambda a: save.write_text("v2") or 0, out=lambda s: None)
        copies = list(self.env.backups.rglob("*.srm"))
        self.assertEqual(["v1"], [c.read_text() for c in copies])

    def test_conflict_keeps_both_and_the_newer_one_is_live(self):
        d = self.env.sync_root / "saves/snes"
        live = self.touch(d / "Final Fantasy VI.srm", "older, picked by syncthing", mtime=1_000)
        self.touch(d / "Final Fantasy VI.sync-conflict-20260920-140355-ABCDEFG.srm", "newer", mtime=2_000)
        [r] = xa.resolve_conflicts(self.env.sync_root)
        self.assertTrue(r.swapped)
        self.assertEqual("newer", live.read_text())
        kept = Path(r.kept_as)
        self.assertEqual("Final Fantasy VI (older save, device ABCDEFG, 2026-09-20 14.03).srm", kept.name)
        self.assertEqual("older, picked by syncthing", kept.read_text())
        self.assertEqual(0, xa.count_conflicts(self.env.sync_root))

    def test_conflict_where_the_live_save_is_already_newer(self):
        d = self.env.sync_root / "states/psx"
        live = self.touch(d / "Final Fantasy IX.state1", "newer", mtime=5_000)
        self.touch(d / "Final Fantasy IX.sync-conflict-20260101-010203-ZZZZZZZ.state1", "older", mtime=4_000)
        [r] = xa.resolve_conflicts(self.env.sync_root)
        self.assertFalse(r.swapped)
        self.assertEqual("newer", live.read_text())
        self.assertEqual("older", Path(r.kept_as).read_text())
        self.assertEqual(2, len(list(d.iterdir())), "nothing was deleted")

    def test_settle_records_conflicts(self):
        index = xa.Index(":memory:")
        d = self.env.sync_root / "saves/gba"
        self.touch(d / "x.srm", "a", mtime=10)
        self.touch(d / "x.sync-conflict-20260920-140355-ABCDEFG.srm", "b", mtime=20)
        v, resolved = xa.settle(FakeSync(snap(peers=[PEER_UP], conflicts=1), snap(peers=[PEER_UP])), index, self.env.sync_root, 0)
        self.assertEqual(1, len(resolved))
        self.assertEqual(1, len(index.recent_conflicts()))
        self.assertEqual("in-sync", v.code)

    def test_never_overwrites_a_newer_save(self):
        old = self.touch(self.home / "in/old.srm", "old", mtime=100)
        dst = self.touch(self.home / "out/old.srm", "new", mtime=200)
        self.assertFalse(xa.copy_if_newer(old, dst))
        self.assertEqual("new", dst.read_text())
        newer = self.touch(self.home / "in/n.srm", "newest", mtime=300)
        self.assertTrue(xa.copy_if_newer(newer, dst))
        self.assertEqual(("newest", 300), (dst.read_text(), int(dst.stat().st_mtime)))

    def test_syncthing_config_has_versioning_and_keeps_conflicts(self):
        xml = xa.syncthing_folder_xml(self.env)
        xa.ET.fromstring(xml)
        self.assertIn('versioning type="staggered"', xml)
        self.assertIn("<maxConflicts>-1</maxConflicts>", xml)
        xa.setup(self.env)
        ignore = (self.env.sync_root / ".stignore").read_text()
        self.assertIn("*.tmp", ignore)
        self.assertNotIn(".srm", ignore)

    def test_pairing_steps_shrink_as_things_become_true(self):
        self.assertEqual(3, len(xa.pairing_steps(self.env, xa.decide(xa.SyncSnapshot()))))
        self.assertEqual(1, len(xa.pairing_steps(self.env, xa.decide(snap(peers=[])))))
        self.assertEqual(0, len(xa.pairing_steps(self.env, xa.decide(snap(peers=[PEER_UP])))))


class SqliteIndex(Tree):
    def test_library_history_and_state_survive_reopen(self):
        xa.setup(self.env)
        self.touch(self.games / "snes" / "Final Fantasy III (USA).sfc")
        index = xa.Index(self.env.db_path)
        index.replace_library(xa.scan(self.env))
        [g] = index.games()
        self.assertEqual((["ff6"], "snes", None), (g["ff"], g["system"], g["last_played"]))
        play = index.start_play(g["id"], "snes9x", now=100.0)
        index.end_play(play, 0, now=200.0)
        index.set_state("verdict", "in-sync")
        index.close()

        index = xa.Index(self.env.db_path)
        self.assertEqual(100.0, index.games()[0]["last_played"])
        self.assertEqual("in-sync", index.get_state("verdict"))
        self.assertEqual(g["id"], index.last_played()["id"])

    def test_a_removed_file_leaves_history_and_comes_back_with_it(self):
        index = xa.Index(":memory:")
        rom = self.touch(self.games / "gba" / "Final Fantasy V Advance.gba")
        index.replace_library(xa.scan(self.env))
        gid = index.games()[0]["id"]
        index.end_play(index.start_play(gid, "mgba"), 0)
        rom.unlink()
        index.replace_library(xa.scan(self.env))
        self.assertEqual([], index.games())
        self.assertIsNone(index.last_played())
        self.touch(self.games / "gba" / "renamed folder" / "Final Fantasy V Advance (Europe).gba")
        index.replace_library(xa.scan(self.env))
        self.assertEqual(gid, index.games()[0]["id"])
        self.assertIsNotNone(index.games()[0]["last_played"])

    def test_id_is_the_same_on_every_machine(self):
        self.assertEqual(xa.game_id("psx", "Final Fantasy VII"), xa.game_id("psx", "final fantasy 7"))
        self.assertNotEqual(xa.game_id("psx", "Final Fantasy VII"), xa.game_id("psx", "Final Fantasy VIII"))


class FirstRun(Tree):
    def test_setup_makes_everything_without_any_games(self):
        xa.setup(self.env)
        for s in xa.SYSTEMS:
            readme = (self.games / s.folder / "README.txt").read_text()
            self.assertIn(s.exts[0], readme)
            self.assertIn("never downloads", readme)
            self.assertTrue((self.env.sync_root / "saves" / s.id).is_dir())
        index = xa.Index(self.env.db_path)
        state = xa.write_state(self.env, index, FakeSync(snap(peers=[], files=0)))
        self.assertEqual([True, False, False], [c["ok"] for c in state["checks"]])
        self.assertEqual([], state["games"])
        self.assertEqual(len(xa.FF_SHELF), len(state["shelf"]))
        self.assertTrue(all(e["games"] == [] for e in state["shelf"]))
        self.assertEqual("no-peers", state["sync"]["code"])
        self.assertIn("never downloads", state["legal"])
        self.assertEqual(state, json.loads(self.env.state_path.read_text()))

    def test_checks_turn_green(self):
        xa.setup(self.env)
        self.install_retroarch("swanstation")
        self.touch(self.games / "psx" / "Final Fantasy VIII (Disc 1).chd")
        index = xa.Index(self.env.db_path)
        index.replace_library(xa.scan(self.env))
        state = xa.build_state(self.env, index, FakeSync())
        self.assertEqual([True, True, True], [c["ok"] for c in state["checks"]])
        ff8 = next(e for e in state["shelf"] if e["key"] == "ff8")
        self.assertEqual([state["games"][0]["id"]], ff8["games"])
        self.assertTrue(state["games"][0]["ready"])

    def test_a_game_without_its_core_names_the_core(self):
        xa.setup(self.env)
        self.install_retroarch("swanstation")
        self.touch(self.games / "gba" / "x.gba")
        index = xa.Index(":memory:")
        index.replace_library(xa.scan(self.env))
        emulator = xa.build_state(self.env, index, FakeSync())["checks"][1]
        self.assertFalse(emulator["ok"])
        self.assertIn('"mgba"', emulator["detail"])


LB_PSX = """<?xml version="1.0" standalone="yes"?>
<LaunchBox>
  <Game>
    <ID>11111111-aaaa-bbbb-cccc-000000000001</ID>
    <Title>Final Fantasy VII</Title>
    <SortTitle>Final Fantasy 07</SortTitle>
    <ReleaseDate>1997-01-31T00:00:00-08:00</ReleaseDate>
    <ApplicationPath>Games\\Sony Playstation\\FF7 d1.chd</ApplicationPath>
    <Platform>Sony Playstation</Platform>
  </Game>
  <Game>
    <ID>11111111-aaaa-bbbb-cccc-000000000002</ID>
    <Title>Final Fantasy Tactics: The Beginning</Title>
    <ApplicationPath>D:\\LaunchBox\\Games\\Sony Playstation\\fft.CHD</ApplicationPath>
    <Platform>Sony Playstation</Platform>
  </Game>
  <Game>
    <ID>11111111-aaaa-bbbb-cccc-000000000003</ID>
    <Title>Not Copied Over</Title>
    <ApplicationPath>E:\\Elsewhere\\missing.chd</ApplicationPath>
    <Platform>Sony Playstation</Platform>
  </Game>
  <AdditionalApplication>
    <GameID>11111111-aaaa-bbbb-cccc-000000000001</GameID>
    <ApplicationPath>Games\\Sony Playstation\\FF7 d3.chd</ApplicationPath>
    <Disc>3</Disc>
    <Name>Disc 3</Name>
  </AdditionalApplication>
  <AdditionalApplication>
    <GameID>11111111-aaaa-bbbb-cccc-000000000001</GameID>
    <ApplicationPath>Games\\Sony Playstation\\FF7 d2.chd</ApplicationPath>
    <Disc>2</Disc>
    <Name>Disc 2</Name>
  </AdditionalApplication>
</LaunchBox>
"""


class LaunchBox(Tree):
    def setUp(self):
        super().setUp()
        self.lb = self.games / "LaunchBox"
        psx = self.lb / "Games" / "Sony Playstation"
        for n in ("FF7 d1.chd", "FF7 d2.chd", "FF7 d3.chd", "fft.chd"):
            self.touch(psx / n)
        self.touch(self.lb / "Games" / "Super Nintendo Entertainment System" / "Final Fantasy III (USA).sfc")
        self.touch(self.lb / "Data" / "Platforms" / "Sony Playstation.xml", LB_PSX)
        self.touch(self.lb / "Emulators" / "RetroArch" / "system" / "not-a-game.bin")

    def test_platform_names_map_case_insensitively(self):
        for name, sid in [("Sony Playstation", "psx"), ("SONY PLAYSTATION 2", "ps2"), ("Sony PSP", "psp"),
                          ("Nintendo Entertainment System", "nes"), ("super nintendo entertainment system", "snes"),
                          ("Nintendo Game Boy Advance", "gba"), ("Nintendo DS", "nds"), ("Nintendo Game Boy Color", "gbc"),
                          ("psx", "psx")]:
            self.assertEqual(sid, xa.platform_system(name).id, name)
        self.assertIsNone(xa.platform_system("Arcade"))

    def test_xml_titles_sort_titles_years_and_discs(self):
        recs = xa.parse_launchbox_platform(self.lb / "Data/Platforms/Sony Playstation.xml", self.lb)
        ff7 = recs[0]
        self.assertEqual(("Final Fantasy VII", "Final Fantasy 07", 1997), (ff7["title"], ff7["sort_title"], ff7["year"]))
        self.assertEqual(["FF7 d1.chd", "FF7 d2.chd", "FF7 d3.chd"], [Path(p).name for p in ff7["discs"]])
        self.assertIsNone(recs[2]["path"], "a game whose file was not copied is simply absent")

    def test_windows_paths_are_found_where_the_library_was_copied(self):
        want = self.lb / "Games/Sony Playstation/fft.chd"
        for raw in ("Games\\Sony Playstation\\fft.chd", "D:\\LaunchBox\\Games\\Sony Playstation\\fft.CHD",
                    "games/sony playstation/FFT.chd", ".\\Games\\Sony Playstation\\fft.chd",
                    "C:\\Users\\someone\\LaunchBox\\Games\\Sony Playstation\\fft.chd"):
            self.assertEqual(want, xa.normalize_lb_path(self.lb, raw), raw)
        self.assertIsNone(xa.normalize_lb_path(self.lb, "E:\\Elsewhere\\missing.chd"))

    def test_the_tree_scans_as_is_with_one_entry_per_game(self):
        games = {g["title"]: g for g in xa.scan(self.env)}
        self.assertEqual({"Final Fantasy VII", "Final Fantasy Tactics: The Beginning", "Final Fantasy III"}, set(games))
        ff7 = games["Final Fantasy VII"]
        self.assertEqual(("psx", 1997, ["ff7"], 3), (ff7["system"], ff7["year"], ff7["ff"], len(ff7["discs"])))
        self.assertEqual(["FF7 d1.chd", "FF7 d2.chd", "FF7 d3.chd"], [Path(x).name for x in Path(ff7["path"]).read_text().splitlines()])
        self.assertEqual(["ff6"], games["Final Fantasy III"]["ff"], "no XML for this platform: the folder name is enough")

    def test_images_with_sanitised_names_and_suffixes(self):
        front = self.lb / "Images" / "Sony Playstation" / "Box - Front"
        art = self.touch(front / "North America" / "Final Fantasy Tactics_ The Beginning-01.png")
        self.touch(front / "Final Fantasy Tactics_ The Beginning-02.png")
        logo = self.touch(self.lb / "Images/Sony Playstation/Clear Logo/Final Fantasy VII-01.jpg")
        games = {g["title"]: g for g in xa.scan(self.env)}
        self.assertEqual(str(art), games["Final Fantasy Tactics: The Beginning"]["boxart"])
        self.assertEqual(str(logo), games["Final Fantasy VII"]["boxart"])
        self.assertIsNone(games["Final Fantasy III"]["boxart"])

    def test_box_front_beats_a_screenshot(self):
        self.touch(self.lb / "Images/Sony Playstation/Screenshot - Gameplay/Final Fantasy VII-01.png")
        front = self.touch(self.lb / "Images/Sony Playstation/Box - Front/Final Fantasy VII-01.png")
        self.assertEqual(str(front), {g["title"]: g for g in xa.scan(self.env)}["Final Fantasy VII"]["boxart"])

    def test_merges_with_the_plain_tree_without_duplicates(self):
        self.touch(self.games / "psx" / "Final Fantasy VII (USA) (Disc 1).chd")
        self.touch(self.games / "psx" / "Final Fantasy VII (USA) (Disc 2).chd")
        self.touch(self.games / "psx" / "Final Fantasy IX (USA).chd")
        titles = [g["title"] for g in xa.scan(self.env)]
        self.assertEqual(1, titles.count("Final Fantasy VII"))
        self.assertIn("Final Fantasy IX", titles)

    def test_a_launchbox_folder_elsewhere_is_one_setup_step(self):
        away = self.home / "mnt/windows/LaunchBox"
        self.touch(away / "Games/Nintendo Game Boy Advance/Final Fantasy Tactics Advance.gba")
        self.touch(away / "Images/Nintendo Game Boy Advance/Box - Front/Final Fantasy Tactics Advance-01.png")
        xa.setup(self.env, launchbox=str(away))
        g = next(g for g in xa.scan(self.env) if g["system"] == "gba")
        self.assertEqual(["ffta"], g["ff"])
        self.assertTrue(g["boxart"].endswith("Advance-01.png"))
        state = xa.build_state(self.env, xa.Index(":memory:"), FakeSync())
        self.assertEqual({str(self.lb), str(away)}, set(state["checks"][0]["launchbox"]["found"]))

    def test_launchbox_only_subfolders_copied_into_the_games_folder(self):
        import shutil
        for part in ("Games", "Data"):
            shutil.move(str(self.lb / part), str(self.games / part))
        (self.games / "Images").mkdir()
        self.assertIn("Final Fantasy VII", [g["title"] for g in xa.scan(self.env)])


class ImportSaves(Tree):
    def test_import_by_game_name_and_core_folder_never_over_a_newer_save(self):
        xa.setup(self.env)
        self.touch(self.games / "snes" / "Final Fantasy III (USA).sfc")
        index = xa.Index(":memory:")
        index.replace_library(xa.scan(self.env))
        ra = self.home / "win/RetroArch"
        self.touch(ra / "saves/Snes9x/Final Fantasy III (USA).srm", "windows save", mtime=500)
        self.touch(ra / "saves/mGBA/Some Other Game.srm", "gba", mtime=500)
        self.touch(ra / "saves/Mystery Core/what.srm", "?", mtime=500)
        self.touch(ra / "States/Snes9x/Final Fantasy III (USA).state1", "state", mtime=500)
        self.touch(ra / "saves/PPSSPP/PSP/SAVEDATA/ULUS10566/DATA.BIN", "psp", mtime=500)
        newer = self.touch(self.env.sync_root / "states/snes/Final Fantasy III (USA).state1", "played here since", mtime=900)

        r = xa.import_saves(self.env, index, ra)
        self.assertEqual("windows save", (self.env.sync_root / "saves/snes/Final Fantasy III (USA).srm").read_text())
        self.assertEqual("gba", (self.env.sync_root / "saves/gba/Some Other Game.srm").read_text())
        self.assertEqual("psp", (self.env.sync_root / "saves/psp/PSP/SAVEDATA/ULUS10566/DATA.BIN").read_text())
        self.assertEqual("played here since", newer.read_text())
        self.assertEqual((3, 1, 1), (len(r["imported"]), len(r["kept_newer"]), len(r["unknown"])))

    def test_save_stems(self):
        for name in ("Game.srm", "Game.state", "Game.state12", "Game.state.auto", "Game.state1.png", "Game.mcd"):
            self.assertEqual("Game", xa.save_stem(name), name)
        self.assertEqual("Game v1.1 (USA)", xa.save_stem("Game v1.1 (USA).srm"))


class Hygiene(unittest.TestCase):
    def test_the_tool_names_no_download_source(self):
        text = _PATH.read_text().lower()
        for word in ("http://", "https://"):
            for hit in [line for line in text.splitlines() if word in line]:
                self.assertIn("127.0.0.1", hit, "the only URL the tool may hold is Syncthing on localhost")


if __name__ == "__main__":
    unittest.main()
