using XivDesktop.Core.Palette;
using XivDesktop.Core.Windows;

namespace XivDesktop.Core.Tests;

public class CalculatorTests
{
    [Theory]
    [InlineData("2+2", 4)]
    [InlineData("2 + 3 * 4", 14)]
    [InlineData("(2 + 3) * 4", 20)]
    [InlineData("2^3^2", 512)]
    [InlineData("2**10", 1024)]
    [InlineData("-2^2", -4)]
    [InlineData("2*-3", -6)]
    [InlineData("10 % 4", 2)]
    [InlineData("7 / 2", 3.5)]
    [InlineData("sqrt(16) + abs(-2)", 6)]
    [InlineData("sqrt 16", 4)]
    [InlineData("max(3, 9) - min(1, 2)", 8)]
    [InlineData("1e3 + 1_000", 2000)]
    [InlineData(".5 * 4", 2)]
    [InlineData("round(2.5)", 3)]
    [InlineData("6 × 7", 42)]
    public void Evaluates(string expr, double expected)
    {
        Assert.True(Calculator.TryEvaluate(expr, out var v, out var err), err);
        Assert.Equal(expected, v, 9);
    }

    [Fact]
    public void ConstantsAndFunctions()
    {
        Assert.True(Calculator.TryEvaluate("2*pi", out var v, out _));
        Assert.Equal(Math.Tau, v, 12);
        Assert.True(Calculator.TryEvaluate("ln(e)", out v, out _));
        Assert.Equal(1, v, 12);
        Assert.True(Calculator.TryEvaluate("log(1000)", out v, out _));
        Assert.Equal(3, v, 12);
    }

    [Theory]
    [InlineData("", "empty")]
    [InlineData("1/0", "division by zero")]
    [InlineData("2+", "incomplete expression")]
    [InlineData("(1+2", "expected ')'")]
    [InlineData("foo(2)", "unknown name 'foo'")]
    [InlineData("System.IO.File", "unknown name 'System'")]
    [InlineData("2 3", "unexpected '3'")]
    [InlineData("max(1)", "max takes 2 arguments")]
    [InlineData("sqrt(-1)", "not a number")]
    [InlineData("10^400", "infinite")]
    public void RefusesWithAReason(string expr, string error)
    {
        Assert.False(Calculator.TryEvaluate(expr, out _, out var err));
        Assert.Equal(error, err);
    }

    [Fact]
    public void RefusesDeepNestingAndLongInput()
    {
        Assert.False(Calculator.TryEvaluate(new string('(', 100) + "1" + new string(')', 100), out _, out var err));
        Assert.Equal("too deeply nested", err);
        Assert.False(Calculator.TryEvaluate(string.Join("+", Enumerable.Repeat("1", 200)), out _, out err));
        Assert.Equal("too long", err);
    }

    [Theory]
    [InlineData("2+2", true)]
    [InlineData("sqrt 2", true)]
    [InlineData("2*pi", true)]
    [InlineData("firefox", false)]
    [InlineData("42", false)]
    [InlineData("pi", false)]
    [InlineData("text editor", false)]
    [InlineData("-5", false)]
    public void LooksLikeMath(string text, bool expected) => Assert.Equal(expected, Calculator.LooksLikeMath(text));

    [Theory]
    [InlineData(4.0, "4")]
    [InlineData(3.5, "3.5")]
    [InlineData(1.0 / 3, "0.333333333333")]
    [InlineData(-0.0000001, "-1E-07")]
    public void Formats(double v, string text) => Assert.Equal(text, Calculator.Format(v));
}

public class FuzzyTests
{
    [Fact]
    public void MatchesInOrderAndReturnsPositions()
    {
        var m = Fuzzy.Match("Text Editor", "ted")!;
        Assert.Equal([0, 5, 6], m.Positions); // T + Ed: the word start is preferred over "Tex(t) (E)(d)"
        Assert.Null(Fuzzy.Match("Firefox", "xf"));
        Assert.Same(FuzzyMatch.All, Fuzzy.Match("anything", " "));
    }

    [Fact]
    public void SubstringsAndWordStartsRankHigher()
    {
        Assert.True(Fuzzy.Match("Firefox", "fire")!.Score > Fuzzy.Match("Files for extra", "fire")!.Score);
        Assert.True(Fuzzy.Match("Move to workspace 3", "work")!.Score > Fuzzy.Match("Network", "work")!.Score);
        Assert.Equal([8, 9, 10, 11], Fuzzy.Match("Move to workspace", "work")!.Positions);
        Assert.True(Fuzzy.Match("foot", "foot")!.Score > Fuzzy.Match("footer", "foot")!.Score);
    }

    [Fact]
    public void IgnoresSpacesAndCase()
    {
        Assert.NotNull(Fuzzy.Match("Workspace 3", "ws 3"));
        Assert.NotNull(Fuzzy.Match("GNOME Calculator", "CALC"));
    }
}

public class PaletteTests
{
    private static AppInfo App(string id, string name, string generic = "", bool terminal = false)
        => new() { Id = id + ".desktop", Name = name, GenericName = generic, Command = id, Terminal = terminal };

    private static readonly PaletteContext Ctx = new()
    {
        Apps =
        [
            App("firefox", "Firefox", "Web Browser"),
            App("org.gnome.TextEditor", "Text Editor"),
            App("htop", "htop", "Process Viewer", terminal: true),
            App("org.gnome.Calculator", "Calculator"),
        ],
        Favourites = ["org.gnome.Calculator.desktop"],
        Recents = ["firefox.desktop"],
        Windows =
        [
            new WindowPanel { Id = 12, Title = "Mozilla Firefox", App = "firefox", State = "live", Kind = "pet", Focused = true },
            new WindowPanel { Id = 13, Title = "btop", App = "foot", State = "live", Kind = "pin" },
            new WindowPanel { Id = 14, Title = "gone", State = "ended", Kind = "pin" },
        ],
        TargetWindow = 12,
        WorkspaceOf = id => id == 13 ? 2 : 1,
        CurrentWorkspace = 1,
    };

    private static List<PaletteItem> Run(string q, PaletteContext? ctx = null) => PaletteEngine.Build(ctx ?? Ctx, q);

    [Theory]
    [InlineData("a:fire", PaletteFilter.Apps, "fire")]
    [InlineData("w: btop", PaletteFilter.Windows, "btop")]
    [InlineData("=2+2", PaletteFilter.Calc, "2+2")]
    [InlineData(">  ask hi", PaletteFilter.Commands, "ask hi")]
    [InlineData("  firefox ", PaletteFilter.All, "firefox")]
    [InlineData("A:x", PaletteFilter.Apps, "x")]
    public void PrefixesSelectProviders(string raw, PaletteFilter filter, string text)
        => Assert.Equal(new PaletteQuery(filter, text), PaletteQuery.Parse(raw));

    [Fact]
    public void EmptyQueryShowsFavouritesRecentsAndWindowsButNoActions()
    {
        var r = Run("");
        Assert.Equal("Calculator", r[0].Title);                       // favourite
        Assert.Equal(PaletteProvider.Window, r[1].Provider);          // focused window
        Assert.Equal("Mozilla Firefox", r[1].Title);
        Assert.Contains(r, i => i.Title == "Firefox" && i.Provider == PaletteProvider.App);
        Assert.DoesNotContain(r, i => i.Provider == PaletteProvider.Action);
        Assert.DoesNotContain(r, i => i.Title == "gone");             // ended panels are not offered
    }

    [Fact]
    public void AppsRankAboveFuzzyActionsForTheirName()
    {
        var r = Run("fire");
        Assert.Equal("Firefox", r[0].Title);
        Assert.Equal(PaletteProvider.App, r[0].Provider);
        Assert.Equal([0, 1, 2, 3], r[0].Highlights);
        Assert.Contains(r, i => i.Provider == PaletteProvider.Window && i.WindowId == 12);
    }

    [Fact]
    public void TerminalAppsAreListedButDisabled()
    {
        var htop = Run("a:htop").Single();
        Assert.False(htop.Enabled);
        Assert.Contains("terminal", htop.Reason);
    }

    [Fact]
    public void ArithmeticGoesToTheTop()
    {
        var r = Run("12*12");
        Assert.Equal(PaletteProvider.Calc, r[0].Provider);
        Assert.Equal("144", r[0].Title);
        Assert.Equal(new PaletteCommand(PaletteCommand.Copy, "144"), r[0].Command);

        var bad = Run("=1/0").Single();
        Assert.False(bad.Enabled);
        Assert.Equal("division by zero", bad.Reason);
        Assert.DoesNotContain(Run("calc"), i => i.Provider == PaletteProvider.Calc);
    }

    [Fact]
    public void WindowFilterMatchesTitleOrApp()
    {
        Assert.Equal([13L], Run("w:btop").Select(i => i.WindowId!.Value));
        Assert.Equal([13L], Run("w:foot").Select(i => i.WindowId!.Value));
        var btop = Run("w:btop")[0];
        Assert.Contains("workspace 2 (hidden)", btop.Subtitle);
        Assert.Equal(new PaletteCommand(PaletteCommand.Focus, Id: 13), btop.Command);
    }

    [Fact]
    public void ActionsTargetTheFocusedPanel()
    {
        var pet = Run(">make pet")[0];
        Assert.Equal(new PaletteCommand(PaletteCommand.Place, "pet", 12), pet.Command);
        var move = Run(">move to workspace 4")[0];
        Assert.Equal(new PaletteCommand(PaletteCommand.Move, Id: 12, Number: 4), move.Command);
        var ws = Run("workspace 2")[0];
        Assert.Equal(new PaletteCommand(PaletteCommand.Workspace, Number: 2), ws.Command);
        Assert.Contains("1 window", ws.Subtitle);

        var none = Run(">close window", Ctx with { TargetWindow = null })[0];
        Assert.False(none.Enabled);
        Assert.Equal("no window panel focused", none.Reason);
    }

    [Fact]
    public void AskAndSlashCommands()
    {
        var ask = Run("ask what is a dalamud")[0];
        Assert.Equal(new PaletteCommand(PaletteCommand.Post, "ask what is a dalamud"), ask.Command);
        Assert.Equal(new PaletteCommand(PaletteCommand.Post, "ask why"), Run(">/ask why")[0].Command);
        Assert.Equal(new PaletteCommand(PaletteCommand.Chat, "/xlplugins"), Run(">/xlplugins")[0].Command);
        Assert.Equal(new PaletteCommand(PaletteCommand.Post, "toggle"), Run(">term")[0].Command);
        Assert.Equal(5, Run(">").Count(i => i.Provider == PaletteProvider.Command));
    }

    [Fact]
    public void WithoutGhosttyEverythingThatNeedsItIsDisabled()
    {
        var r = Run("fire", Ctx with { GhosttyAvailable = false, WindowsAvailable = false, Windows = [], TargetWindow = null });
        Assert.False(r.First(i => i.Title == "Firefox").Enabled);
        Assert.All(Run(">workspace", Ctx with { WindowsAvailable = false }).Where(i => i.Command.Kind == PaletteCommand.Workspace), i => Assert.False(i.Enabled));
    }

    [Fact]
    public void RespectsTheLimit() => Assert.True(PaletteEngine.Build(Ctx, ">", max: 3).Count == 3);
}
