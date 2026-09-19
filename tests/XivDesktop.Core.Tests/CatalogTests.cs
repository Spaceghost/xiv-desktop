namespace XivDesktop.Core.Tests;

public class CatalogTests
{
    private static void Install(TempTree t, string dir, string sample, string? asName = null)
        => t.Write($"{dir}/{asName ?? sample}", Samples.Read(sample));

    [Fact]
    public void ScansAllSamplesAndAppliesVisibilityRules()
    {
        using var t = new TempTree();
        foreach (var f in Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "Samples"), "*.desktop"))
            Install(t, "/usr/share/applications", Path.GetFileName(f));

        var c = AppCatalog.Scan(t.Paths);
        var ids = c.Apps.Select(a => a.Id).ToList();

        Assert.Contains("basic.desktop", ids);
        Assert.Contains("terminal.desktop", ids); // listed, but LaunchPlan refuses it
        Assert.DoesNotContain("nodisplay.desktop", ids);
        Assert.DoesNotContain("onlyshowin.desktop", ids);
        Assert.DoesNotContain("link.desktop", ids);
        Assert.Equal(1, c.Stats.NoDisplay);
        Assert.Equal(1, c.Stats.ShowInFiltered);
        Assert.Equal(1, c.Stats.NotApplications);
        Assert.Equal(1, c.Stats.Terminal);
        Assert.Equal(c.Apps.OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase).Select(a => a.Id), ids);
        Assert.Equal("gnome-text-editor", c.Find("basic")!.Command);
    }

    [Fact]
    public void LaterDirectoriesOverrideEarlierOnesById()
    {
        using var t = new TempTree();
        t.Write("/usr/share/applications/app.desktop", "[Desktop Entry]\nType=Application\nName=System\nExec=sys\n");
        t.Write("/home/user/.local/share/applications/app.desktop", "[Desktop Entry]\nType=Application\nName=User\nExec=user\n");
        t.Write("/var/lib/flatpak/exports/share/applications/other.desktop", "[Desktop Entry]\nType=Application\nName=Other\nExec=other\n");

        var c = AppCatalog.Scan(t.Paths);
        Assert.Equal(2, c.Apps.Count);
        Assert.Equal("User", c.Find("app.desktop")!.Name);
        Assert.Equal(1, c.Stats.Overridden);
    }

    [Fact]
    public void HiddenOverrideRemovesTheEntry()
    {
        using var t = new TempTree();
        t.Write("/usr/share/applications/app.desktop", "[Desktop Entry]\nType=Application\nName=System\nExec=sys\n");
        t.Write("/home/user/.local/share/applications/app.desktop", "[Desktop Entry]\nHidden=true\n");

        var c = AppCatalog.Scan(t.Paths);
        Assert.Empty(c.Apps);
        Assert.Equal(1, c.Stats.HiddenKey);
    }

    [Fact]
    public void SubdirectoriesBecomeDashedIds()
    {
        using var t = new TempTree();
        t.Write("/usr/share/applications/kde4/foo.desktop", "[Desktop Entry]\nType=Application\nName=Foo\nExec=foo\n");
        var c = AppCatalog.Scan(t.Paths);
        Assert.Equal("kde4-foo.desktop", Assert.Single(c.Apps).Id);
    }

    [Fact]
    public void TryExecHidesMissingPrograms()
    {
        using var t = new TempTree();
        t.Write("/usr/bin/present", "#!/bin/sh\n");
        t.Write("/usr/share/applications/a.desktop", "[Desktop Entry]\nType=Application\nName=A\nExec=present\nTryExec=present\n");
        t.Write("/usr/share/applications/b.desktop", "[Desktop Entry]\nType=Application\nName=B\nExec=absent\nTryExec=absent\n");
        t.Write("/usr/share/applications/c.desktop", "[Desktop Entry]\nType=Application\nName=C\nExec=x\nTryExec=/usr/bin/present\n");

        var c = AppCatalog.Scan(t.Paths);
        Assert.Equal(["A", "C"], c.Apps.Select(a => a.Name));
        Assert.Equal(1, c.Stats.TryExecMissing);
    }

    [Fact]
    public void MissingDirectoriesAreNotErrors()
    {
        using var t = new TempTree();
        var c = AppCatalog.Scan(t.Paths);
        Assert.Empty(c.Apps);
        Assert.Empty(c.Errors);
    }

    [Fact]
    public void ResolveByIdNameOrQuery()
    {
        using var t = new TempTree();
        t.Write("/usr/share/applications/org.gnome.TextEditor.desktop", Samples.Read("basic.desktop"));
        t.Write("/usr/share/applications/calc.desktop", Samples.Read("localized-only.desktop"));
        var c = AppCatalog.Scan(t.Paths);

        Assert.Equal("org.gnome.TextEditor.desktop", c.Resolve("org.gnome.TextEditor")!.Id);
        Assert.Equal("org.gnome.TextEditor.desktop", c.Resolve("org.gnome.TextEditor.desktop")!.Id);
        Assert.Equal("calc.desktop", c.Resolve("calculator")!.Id);
        Assert.Equal("calc.desktop", c.Resolve("math")!.Id);
        Assert.Equal("org.gnome.TextEditor.desktop", c.Resolve("txed")!.Id);
        Assert.Null(c.Resolve("zzzz"));
        Assert.Null(c.Resolve("  "));
    }

    [Fact]
    public void ShownInRules()
    {
        var e = new DesktopEntry { Id = "x", OnlyShowIn = ["XivDesktop"] };
        Assert.True(AppCatalog.ShownIn(e, AppCatalog.DesktopName));
        Assert.False(AppCatalog.ShownIn(e with { NotShowIn = ["XivDesktop"] }, AppCatalog.DesktopName));
        Assert.True(AppCatalog.ShownIn(new DesktopEntry { Id = "y", NotShowIn = ["GNOME"] }, AppCatalog.DesktopName));
        Assert.False(AppCatalog.ShownIn(new DesktopEntry { Id = "g", OnlyShowIn = ["GNOME"] }, AppCatalog.DesktopName));
        Assert.False(AppCatalog.ShownIn(new DesktopEntry { Id = "gu", OnlyShowIn = ["GNOME", "Unity"] }, AppCatalog.DesktopName));
        Assert.False(AppCatalog.ShownIn(new DesktopEntry { Id = "k", OnlyShowIn = ["KDE"] }, AppCatalog.DesktopName));
        Assert.True(AppCatalog.ShownIn(new DesktopEntry { Id = "multi", OnlyShowIn = ["GNOME", "Unity", "XFCE", "KDE"] }, AppCatalog.DesktopName));
    }
}
