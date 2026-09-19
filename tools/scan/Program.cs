// dotnet run --project tools/scan [-- --all] [-- --missing]
// Scans this host's application directories directly (Linux paths, not Z:\) with the same code the plugin
// runs under Wine, and prints counts plus a sample of apps with their command and icon.
using XivDesktop.Core;

var all = args.Contains("--all");
var showMissing = args.Contains("--missing");
var home = Environment.GetEnvironmentVariable("HOME") ?? "";
var paths = HostPaths.Native(home);

var sw = System.Diagnostics.Stopwatch.StartNew();
var catalog = AppCatalog.Scan(paths);
sw.Stop();

var s = catalog.Stats;
Console.WriteLine($"scanned in {sw.ElapsedMilliseconds} ms: {s.FilesRead} .desktop files, {s.Overridden} overridden by a later directory");
Console.WriteLine($"apps listed: {catalog.Apps.Count} ({s.Terminal} with Terminal=true, not launchable in v0)");
Console.WriteLine($"hidden: NoDisplay {s.NoDisplay}, Hidden {s.HiddenKey}, OnlyShowIn/NotShowIn {s.ShowInFiltered}, TryExec missing {s.TryExecMissing}, not Type=Application {s.NotApplications}, bad Exec {s.BadExec}");
Console.WriteLine($"icons: {s.IconsPng} PNG, {s.IconsSvgOnly} SVG-only (letter tile), {s.IconsMissing} not found (letter tile)");
foreach (var e in catalog.Errors)
    Console.WriteLine("error: " + e);
Console.WriteLine();

IEnumerable<AppInfo> sample = all ? catalog.Apps : Spread(catalog.Apps, 10);
foreach (var app in sample)
    Print(app);

if (showMissing)
{
    Console.WriteLine();
    Console.WriteLine("icons not resolved to PNG:");
    foreach (var app in catalog.Apps.Where(a => a.IconKind != IconKind.Png))
        Console.WriteLine($"  {app.IconKind,-8} {app.Id}  Icon={app.Icon}");
}

static void Print(AppInfo app)
{
    var icon = app.IconKind switch
    {
        IconKind.Png => app.IconPath,
        IconKind.SvgOnly => $"(svg only: {app.Icon}; letter tile)",
        _ => $"(not found: {(app.Icon.Length > 0 ? app.Icon : "no Icon=")}; letter tile)",
    };
    Console.WriteLine($"{app.Name}  [{app.Id}]{(app.Terminal ? "  Terminal=true" : "")}");
    Console.WriteLine($"  run:  {app.Command}");
    Console.WriteLine($"  icon: {icon}");
}

// Evenly spaced picks so the sample is not ten apps starting with "A".
static IEnumerable<AppInfo> Spread(IReadOnlyList<AppInfo> apps, int n)
{
    if (apps.Count <= n)
        return apps;
    return Enumerable.Range(0, n).Select(i => apps[i * apps.Count / n]);
}
