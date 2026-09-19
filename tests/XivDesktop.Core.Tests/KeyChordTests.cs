using XivDesktop.Core.Input;

namespace XivDesktop.Core.Tests;

public class KeyChordTests
{
    [Theory]
    [InlineData("Super+D", KeyMods.Super, 'D')]
    [InlineData("win+shift+1", KeyMods.Super | KeyMods.Shift, '1')]
    [InlineData("Mod4+Return", KeyMods.Super, 0x0D)]
    [InlineData("Ctrl+Alt+Space", KeyMods.Ctrl | KeyMods.Alt, 0x20)]
    [InlineData("alt + shift + left", KeyMods.Alt | KeyMods.Shift, 0x25)]
    [InlineData("F12", KeyMods.None, 0x7B)]
    [InlineData("Control+Esc", KeyMods.Ctrl, 0x1B)]
    [InlineData("Super+Num5", KeyMods.Super, 0x65)]
    [InlineData("Super+/", KeyMods.Super, 0xBF)]
    public void Parses(string text, KeyMods mods, int key)
    {
        Assert.True(KeyChord.TryParse(text, out var c));
        Assert.Equal(new KeyChord(mods, key), c);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Super")]
    [InlineData("Super+")]
    [InlineData("Super++")]
    [InlineData("D+Super")]
    [InlineData("Super+D+E")]
    [InlineData("Shift+Shift+A")]
    [InlineData("Hyper+D")]
    [InlineData("F25")]
    public void RefusesNonsense(string text) => Assert.False(KeyChord.TryParse(text, out _));

    [Theory]
    [InlineData("super+shift+ctrl+alt+q", "Super+Ctrl+Alt+Shift+Q")]
    [InlineData("win+return", "Super+Enter")]
    [InlineData("Alt+Shift+Right", "Alt+Shift+Right")]
    public void FormatsCanonically(string text, string canonical)
    {
        var c = KeyChord.Parse(text);
        Assert.Equal(canonical, c.ToString());
        Assert.Equal(c, KeyChord.Parse(c.ToString()));
    }

    private static Func<int, bool> Down(params int[] keys) => vk => keys.Contains(vk);

    [Fact]
    public void ModifiersMustMatchExactly()
    {
        var super1 = KeyChord.Parse("Super+1");
        Assert.True(super1.IsHeld(Down(KeyChord.VkLWin, '1')));
        Assert.True(super1.IsHeld(Down(KeyChord.VkRWin, '1')));
        Assert.False(super1.IsHeld(Down(KeyChord.VkLWin, KeyChord.VkShift, '1')));
        Assert.False(super1.IsHeld(Down('1')));
        Assert.True(KeyChord.Parse("Shift+A").IsHeld(Down(KeyChord.VkRShift, 'A')));
    }

    [Fact]
    public void DefaultsAreSwayLikeAndConflictFree()
    {
        foreach (var preset in Enum.GetValues<ModifierPreset>())
        {
            var bindings = Keybinds.Compile(Keybinds.Defaults(preset));
            Assert.Equal(KeyAction.All.Count, bindings.Count);
            Assert.Empty(Keybinds.Conflicts(bindings));
        }

        var d = Keybinds.Defaults();
        Assert.Equal("Super+D", d[KeyAction.Launcher]);
        Assert.Equal("Super+Enter", d[KeyAction.Terminal]);
        Assert.Equal("Super+Q", d[KeyAction.Close]);
        Assert.Equal("Super+Space", d[KeyAction.TogglePet]);
        Assert.Equal("Super+Shift+Space", d[KeyAction.PinFront]);
        Assert.Equal("Super+Left", d[KeyAction.FocusPrev]);
        Assert.Equal("Super+3", d[KeyAction.Workspace(3)]);
        Assert.Equal("Super+Shift+9", d[KeyAction.MoveTo(9)]);

        var alt = Keybinds.Defaults(ModifierPreset.AltShift);
        Assert.Equal("Alt+Shift+D", alt[KeyAction.Launcher]);
        Assert.Equal("Ctrl+Alt+Shift+1", alt[KeyAction.MoveTo(1)]);
    }

    [Fact]
    public void CompileSkipsBadEntriesAndReportsThem()
    {
        var errors = new List<string>();
        var b = Keybinds.Compile(new Dictionary<string, string> { [KeyAction.Launcher] = "Super+D", [KeyAction.Close] = "Super+Nope", [KeyAction.Terminal] = "" }, errors);
        Assert.Equal(KeyAction.Launcher, Assert.Single(b).Action);
        Assert.Contains("close", Assert.Single(errors));
    }

    [Fact]
    public void WorkspaceActionNumbers()
    {
        Assert.Equal(4, KeyAction.WorkspaceNumber("workspace-4", out var move));
        Assert.False(move);
        Assert.Equal(9, KeyAction.WorkspaceNumber("move-9", out move));
        Assert.True(move);
        Assert.Equal(0, KeyAction.WorkspaceNumber("workspace-10", out _));
        Assert.Equal(0, KeyAction.WorkspaceNumber("launcher", out _));
    }

    [Fact]
    public void TrackerFiresOncePerPressAndNotWhileBlocked()
    {
        var t = new ChordTracker { Bindings = Keybinds.Compile(Keybinds.Defaults()) };
        var superD = Down(KeyChord.VkLWin, 'D');

        var (fired, consumed) = t.Update(superD, blocked: false);
        Assert.Equal([KeyAction.Launcher], fired);
        Assert.Equal(['D'], consumed);

        (fired, consumed) = t.Update(superD, blocked: false); // still held: swallowed, not repeated
        Assert.Empty(fired);
        Assert.Equal(['D'], consumed);

        (fired, _) = t.Update(Down(KeyChord.VkLWin), blocked: false);
        Assert.Empty(fired);
        (fired, _) = t.Update(superD, blocked: false);
        Assert.Equal([KeyAction.Launcher], fired);

        // Pressed while a text field has focus: nothing fires, not even after the field lets go.
        t.Update(Down(), blocked: false);
        (fired, consumed) = t.Update(superD, blocked: true);
        Assert.Empty(fired);
        Assert.Empty(consumed);
        (fired, _) = t.Update(superD, blocked: false);
        Assert.Empty(fired);
    }

    [Fact]
    public void TrackerTellsShiftVariantsApart()
    {
        var t = new ChordTracker { Bindings = Keybinds.Compile(Keybinds.Defaults()) };
        Assert.Equal([KeyAction.MoveTo(2)], t.Update(Down(KeyChord.VkLWin, KeyChord.VkShift, '2'), false).Fired);
        t.Update(Down(), false);
        Assert.Equal([KeyAction.Workspace(2)], t.Update(Down(KeyChord.VkLWin, '2'), false).Fired);
    }
}
