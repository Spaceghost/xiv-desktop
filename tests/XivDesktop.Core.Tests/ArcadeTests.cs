using XivDesktop.Core.Arcade;
using XivDesktop.Core.Palette;

namespace XivDesktop.Core.Tests;

public sealed class ArcadeTests
{
    private const string Helper = "/home/user/.config/xiv-arcade/xiv-arcade";

    private const string StateJson = """
        {
          "version": 1, "at": 1789900000.5, "legal": "bring your own", "message": "Sync checked", "playing": null,
          "games_root": "/home/user/Games/Arcade", "sync_root": "/home/user/.local/share/xiv-arcade/sync",
          "checks": [
            {"key": "folder", "title": "Choose your games folder", "ok": true, "detail": "/home/user/Games/Arcade", "command": "xiv-arcade setup",
             "launchbox": {"text": "I have a LaunchBox folder", "command": "xiv-arcade setup --launchbox /path", "found": ["/home/user/Games/Arcade/LaunchBox"]}},
            {"key": "emulator", "title": "Install the emulator", "ok": false, "detail": "RetroArch is not installed.", "command": "flatpak install -y flathub org.libretro.RetroArch"},
            {"key": "games", "title": "Drop your own game files in", "ok": true, "detail": "2 game(s) found.", "command": "xdg-open /home/user/Games/Arcade"}
          ],
          "sync": {"code": "pulling", "label": "Syncing…", "detail": "Newer saves are still arriving.", "can_launch": false,
                   "steps": [{"text": "Pair it", "command": "xiv-arcade pair DEVICE-ID"}],
                   "conflicts": [{"live": "/s/a.srm", "kept_as": "/s/a (older save, device ABCDEFG, 2026-09-20 14.03).srm"}]},
          "systems": [{"id": "psx", "name": "PlayStation", "folder": "psx", "exts": [".cue", ".chd"], "core": "swanstation", "hint": "h", "note": "n"},
                      {"id": "snes", "name": "Super NES", "folder": "snes", "exts": [".sfc"], "core": null, "hint": "get snes9x", "note": ""}],
          "shelf": [{"key": "ff7", "title": "Final Fantasy VII", "year": 1997, "platforms": "PS1", "games": ["aaa"]},
                    {"key": "ff8", "title": "Final Fantasy VIII", "year": 1999, "platforms": "PS1", "games": []}],
          "games": [{"id": "aaa", "title": "Final Fantasy VII", "year": 1997, "system": "psx", "path": "/p/ff7.m3u", "discs": 3, "ff": ["ff7"], "boxart": null, "last_played": 1789800000.0, "ready": true},
                    {"id": "bbb", "title": "Chrono Trigger", "year": null, "system": "snes", "path": "/p/ct.sfc", "discs": 0, "ff": [], "boxart": "/p/ct.png", "last_played": null, "ready": false}],
          "last": "aaa"
        }
        """;

    [Fact]
    public void StateParses()
    {
        var s = ArcadeState.Parse(StateJson);
        Assert.True(s.Loaded);
        Assert.False(s.SetupDone);
        Assert.Equal([true, false, true], s.Checks.Select(c => c.Ok));
        Assert.Equal(["/home/user/Games/Arcade/LaunchBox"], s.LaunchBoxFound);
        Assert.Equal("pulling", s.Sync.Code);
        Assert.False(s.Sync.CanLaunch);
        Assert.Equal("busy", s.Sync.Tone);
        Assert.Equal("xiv-arcade pair DEVICE-ID", s.Sync.Steps.Single().Command);
        Assert.Equal("a (older save, device ABCDEFG, 2026-09-20 14.03).srm", s.Sync.Conflicts.Single());
        Assert.Equal(3, s.Game("aaa")!.Discs);
        Assert.Equal("Final Fantasy VII", s.Game(s.Last)!.Title);
        Assert.Equal("PlayStation", s.SystemName("psx"));
        Assert.Equal("Chrono Trigger", s.EverythingElse().Single().Games.Single().Title);
        Assert.Equal("/p/ct.png", s.Game("bbb")!.Boxart);
        Assert.Null(s.PlayingTitle);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[1,2]")]
    [InlineData("{\"games\": 7, \"checks\": \"x\", \"sync\": []}")]
    public void BrokenStateIsEmptyNotAnException(string? json)
    {
        var s = ArcadeState.Parse(json);
        Assert.False(s.Loaded);
        Assert.Empty(s.Games);
        Assert.True(s.Sync.CanLaunch);
        Assert.Contains("never downloads", s.Legal, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("", ArcadeVerb.Open, "")]
    [InlineData("  ", ArcadeVerb.Open, "")]
    [InlineData("last", ArcadeVerb.Last, "")]
    [InlineData("list", ArcadeVerb.List, "")]
    [InlineData("sync", ArcadeVerb.Sync, "")]
    [InlineData("setup", ArcadeVerb.Setup, "")]
    [InlineData("rescan", ArcadeVerb.Rescan, "")]
    [InlineData("ff7", ArcadeVerb.Play, "ff7")]
    [InlineData("final fantasy tactics", ArcadeVerb.Play, "final fantasy tactics")]
    [InlineData("last remnant", ArcadeVerb.Play, "last remnant")]
    [InlineData("launch ff9", ArcadeVerb.Play, "ff9")]
    [InlineData("launch --dry-run /g/gba/x.gba", ArcadeVerb.DryRun, "/g/gba/x.gba")]
    [InlineData("launchbox /mnt/win/LaunchBox", ArcadeVerb.LaunchBox, "/mnt/win/LaunchBox")]
    [InlineData("import-saves /mnt/win/RetroArch", ArcadeVerb.ImportSaves, "/mnt/win/RetroArch")]
    public void RequestsParse(string text, ArcadeVerb verb, string arg)
        => Assert.Equal(new ArcadeRequest(verb, arg), ArcadeRequest.Parse(text));

    [Fact]
    public void HelperLinesAreQuotedForTheShell()
    {
        Assert.Equal($"python3 '{Helper}' launch 'aaa'", ArcadeCommands.For(Helper, new ArcadeRequest(ArcadeVerb.Play, "ff7"), "aaa"));
        Assert.Equal($"python3 '{Helper}' last", ArcadeCommands.For(Helper, new ArcadeRequest(ArcadeVerb.Last)));
        Assert.Equal($"python3 '{Helper}' sync", ArcadeCommands.For(Helper, new ArcadeRequest(ArcadeVerb.Sync)));
        Assert.Equal($"python3 '{Helper}' setup '--launchbox' '/mnt/it''s here/LaunchBox; rm -rf ~'".Replace("''", "'\\''", StringComparison.Ordinal),
            ArcadeCommands.For(Helper, ArcadeRequest.Parse("launchbox /mnt/it's here/LaunchBox; rm -rf ~")));
        Assert.Equal($"python3 '{Helper}' launch '--dry-run' '$(reboot)'", ArcadeCommands.For(Helper, ArcadeRequest.Parse("launch --dry-run $(reboot)")));
        Assert.Null(ArcadeCommands.For(Helper, new ArcadeRequest(ArcadeVerb.Play, "ff7")));
        Assert.Null(ArcadeCommands.For(Helper, new ArcadeRequest(ArcadeVerb.Open)));
        Assert.Null(ArcadeCommands.Line(Helper, "launch", "a\nb"));
        Assert.Equal(["python3", Helper, "import-saves", "/x y"], ArcadeCommands.Argv(Helper, "import-saves", "/x y"));
    }

    private static readonly ArcadeGame[] Library =
    [
        Game("Final Fantasy VII"), Game("Final Fantasy VIII"), Game("Final Fantasy Tactics"), Game("Final Fantasy Tactics Advance"),
        Game("Final Fantasy X"), Game("Final Fantasy X-2"), Game("Final Fantasy IV"), Game("Final Fantasy VI Advance"), Game("Chrono Cross", ff: false),
    ];

    private static ArcadeGame Game(string title, bool ff = true, bool ready = true)
        => new(title.ToLowerInvariant(), title, null, "psx", "/p/" + title, 0, ff ? ["x"] : [], null, null, ready);

    [Theory]
    [InlineData("ff7", "Final Fantasy VII")]
    [InlineData("FF VII", "Final Fantasy VII")]
    [InlineData("final fantasy 7", "Final Fantasy VII")]
    [InlineData("ff8", "Final Fantasy VIII")]
    [InlineData("tactics", "Final Fantasy Tactics")]
    [InlineData("tactics adv", "Final Fantasy Tactics Advance")]
    [InlineData("ffx", "Final Fantasy X")]
    [InlineData("ffx-2", "Final Fantasy X-2")]
    [InlineData("x-2", "Final Fantasy X-2")]
    [InlineData("ff6", "Final Fantasy VI Advance")]
    [InlineData("ff 4", "Final Fantasy IV")]
    [InlineData("chrono", "Chrono Cross")]
    [InlineData("crss", "Chrono Cross")]
    public void FuzzyPick(string query, string expected) => Assert.Equal(expected, ArcadeCommands.Pick(Library, query)?.Title);

    [Fact]
    public void NoMatchIsNull() => Assert.Null(ArcadeCommands.Pick(Library, "zelda"));

    [Fact]
    public void PaletteFindsGamesOnlyOnceSomethingIsTyped()
    {
        var ctx = new PaletteContext { ArcadeGames = [Game("Final Fantasy VII"), Game("Final Fantasy IX", ready: false)], ArcadeSystemName = _ => "PlayStation" };
        Assert.DoesNotContain(PaletteEngine.Build(ctx, ""), i => i.Command.Kind == PaletteCommand.Arcade);

        var row = PaletteEngine.Build(ctx, "ff7").First(i => i.Command.Kind == PaletteCommand.Arcade);
        Assert.Equal(("Final Fantasy VII", "final fantasy vii", "Arcade", true), (row.Title, row.Command.Arg, row.Badge, row.Enabled));
        Assert.StartsWith("PlayStation", row.Subtitle, StringComparison.Ordinal);

        var missing = PaletteEngine.Build(ctx, "ff9").First(i => i.Command.Kind == PaletteCommand.Arcade);
        Assert.False(missing.Enabled);
        Assert.Contains("core", missing.Reason, StringComparison.Ordinal);

        var open = PaletteEngine.Build(ctx, "arc").First(i => i.Command.Kind == PaletteCommand.Arcade);
        Assert.Equal(("Arcade", ""), (open.Title, open.Command.Arg));
    }
}
