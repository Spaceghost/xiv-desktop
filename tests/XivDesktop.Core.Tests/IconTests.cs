namespace XivDesktop.Core.Tests;

public class IconTests
{
    [Fact]
    public void AbsolutePngPath()
    {
        using var t = new TempTree();
        var local = t.Write("/opt/app/icon.png");
        Assert.Equal((IconKind.Png, local), new IconResolver(t.Paths).Resolve("/opt/app/icon.png"));
    }

    [Fact]
    public void AbsoluteSvgIsSvgOnly()
    {
        using var t = new TempTree();
        t.Write("/opt/app/icon.svg");
        Assert.Equal(IconKind.SvgOnly, new IconResolver(t.Paths).Resolve("/opt/app/icon.svg").Kind);
    }

    [Fact]
    public void PrefersLargestHicolorSize()
    {
        using var t = new TempTree();
        t.Write("/usr/share/icons/hicolor/48x48/apps/foo.png");
        var big = t.Write("/usr/share/icons/hicolor/256x256/apps/foo.png");
        Assert.Equal(big, new IconResolver(t.Paths).Resolve("foo").Path);
    }

    [Fact]
    public void HicolorBeforeAdwaitaAndUserRootAfterSystem()
    {
        using var t = new TempTree();
        var sys = t.Write("/usr/share/icons/hicolor/64x64/apps/foo.png");
        t.Write("/usr/share/icons/Adwaita/256x256/apps/foo.png");
        t.Write("/home/user/.local/share/icons/hicolor/256x256/apps/foo.png");
        Assert.Equal(sys, new IconResolver(t.Paths).Resolve("foo").Path);
    }

    [Fact]
    public void FindsAdwaitaLegacyAndFlatpakAndUserIcons()
    {
        using var t = new TempTree();
        var adw = t.Write("/usr/share/icons/Adwaita/96x96/legacy/utilities-terminal.png");
        var flat = t.Write("/var/lib/flatpak/exports/share/icons/hicolor/128x128/apps/com.example.App.png");
        var user = t.Write("/home/user/.local/share/icons/hicolor/48x48/apps/mine.png");
        var r = new IconResolver(t.Paths);
        Assert.Equal(adw, r.Resolve("utilities-terminal").Path);
        Assert.Equal(flat, r.Resolve("com.example.App").Path);
        Assert.Equal(user, r.Resolve("mine").Path);
    }

    [Fact]
    public void AdwaitaLegacyAnd512Fallback()
    {
        using var t = new TempTree();
        var legacy = t.Write("/usr/share/icons/AdwaitaLegacy/48x48/legacy/applications-games.png");
        var huge = t.Write("/var/lib/flatpak/exports/share/icons/hicolor/512x512/apps/md.example.Big.png");
        t.Write("/usr/share/icons/hicolor/48x48/apps/both.png");
        t.Write("/usr/share/icons/hicolor/512x512/apps/both.png");
        var r = new IconResolver(t.Paths);
        Assert.Equal(legacy, r.Resolve("applications-games").Path);
        Assert.Equal(huge, r.Resolve("md.example.Big").Path);
        Assert.EndsWith("48x48/apps/both.png", r.Resolve("both").Path);
    }

    [Fact]
    public void BreezeLayout()
    {
        using var t = new TempTree();
        var png = t.Write("/usr/share/icons/breeze/apps/48/kfoo.png");
        Assert.Equal(png, new IconResolver(t.Paths).Resolve("kfoo").Path);
    }

    [Fact]
    public void SizesBelow48AreIgnored()
    {
        using var t = new TempTree();
        t.Write("/usr/share/icons/hicolor/32x32/apps/small.png");
        Assert.Equal(IconKind.Missing, new IconResolver(t.Paths).Resolve("small").Kind);
    }

    [Fact]
    public void ScalableSvgOnlyIsReported()
    {
        using var t = new TempTree();
        t.Write("/usr/share/icons/hicolor/scalable/apps/vec.svg");
        Assert.Equal((IconKind.SvgOnly, null), new IconResolver(t.Paths).Resolve("vec"));
    }

    [Fact]
    public void PixmapsFallbackAndExtensionInName()
    {
        using var t = new TempTree();
        var pix = t.Write("/usr/share/pixmaps/old.png");
        var r = new IconResolver(t.Paths);
        Assert.Equal(pix, r.Resolve("old").Path);
        Assert.Equal(pix, r.Resolve("old.png").Path);
        Assert.Equal(IconKind.Missing, r.Resolve("nothing").Kind);
        Assert.Equal(IconKind.Missing, r.Resolve("").Kind);
    }

    [Fact]
    public void CatalogCountsIconOutcomes()
    {
        using var t = new TempTree();
        t.Write("/usr/share/icons/hicolor/128x128/apps/org.gnome.TextEditor.png");
        t.Write("/usr/share/icons/hicolor/scalable/apps/quoting.svg");
        t.Write("/usr/share/applications/basic.desktop", Samples.Read("basic.desktop"));
        t.Write("/usr/share/applications/quoting.desktop", Samples.Read("quoting.desktop"));
        t.Write("/usr/share/applications/calc.desktop", Samples.Read("localized-only.desktop"));
        var c = AppCatalog.Scan(t.Paths);
        Assert.Equal(1, c.Stats.IconsPng);
        Assert.Equal(1, c.Stats.IconsSvgOnly);
        Assert.Equal(1, c.Stats.IconsMissing);
        Assert.Equal(IconKind.Png, c.Find("basic")!.IconKind);
    }

    [Fact]
    public void WineMappingProducesZDrivePaths()
    {
        var p = HostPaths.Wine("/home/user");
        Assert.Equal(@"Z:\usr\share\applications", p.ToLocal("/usr/share/applications"));
        Assert.Contains("/home/user/.local/share/applications", p.ApplicationDirs);
    }
}
