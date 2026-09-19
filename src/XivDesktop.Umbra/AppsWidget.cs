// "Apps" toolbar widget: click opens a compact launcher popup, right-click opens the full /desktop window.
using Dalamud.Bindings.ImGui;
using Umbra.Common;
using Umbra.Widgets;
using Una.Drawing;

namespace Umbra.XivDesktop;

[ToolbarWidget(
    "XivDesktopApps",
    "Apps",
    "Launch Linux desktop apps into the game world through XivDesktop (search, favourites, recents). Needs the XivDesktop and ghostty-dalamud Dalamud plugins.",
    ["apps", "launcher", "desktop", "xivdesktop", "linux", "ghostty"])]
public sealed class AppsWidget(
    WidgetInfo info,
    string? guid = null,
    Dictionary<string, object>? configValues = null
) : StandardToolbarWidget(info, guid, configValues)
{
    /// <summary>Game icon shown by default; changeable in the widget's icon settings.</summary>
    public const uint DefaultIconId = 35;

    private const string CvarLabel = "Label";
    private const string CvarDesaturate = "DesaturateWhenUnavailable";

    private XivDesktopClient? _client;

    protected override StandardWidgetFeatures Features =>
        StandardWidgetFeatures.Text
        | StandardWidgetFeatures.Icon
        | StandardWidgetFeatures.CustomizableIcon;

    protected override uint DefaultGameIconId => DefaultIconId;

    public override WidgetPopup Popup { get; } = new AppsPopup();

    protected override IEnumerable<IWidgetConfigVariable> GetConfigVariables() =>
    [
        .. base.GetConfigVariables(),
        new StringWidgetConfigVariable(CvarLabel, "Label", "Text on the toolbar button.", "Apps", 32),
        new BooleanWidgetConfigVariable(CvarDesaturate, "Grey icon when unavailable",
            "Desaturate the icon while XivDesktop or ghostty-dalamud is missing.", true),
    ];

    protected override void OnLoad()
    {
        _client = Framework.Service<XivDesktopClient>();
        Node.OnRightClick += OnRightClick;
    }

    protected override void OnUnload()
    {
        Node.OnRightClick -= OnRightClick;
        _client = null;
    }

    protected override void OnDraw()
    {
        var client = _client;
        if (client is null) return;
        SetText(GetConfigValue<string>(CvarLabel) is { Length: > 0 } label ? label : "Apps");
        if (client.State != DesktopLinkState.Ready && GetConfigValue<bool>(CvarDesaturate)) SetIconDesaturated(true);
        SetTooltip(client.State switch
        {
            DesktopLinkState.Missing => "XivDesktop is not loaded.",
            DesktopLinkState.NoGhostty => client.Status?.Summary ?? "ghostty-dalamud is not loaded.",
            _ => (client.Status?.Summary ?? "") + "\nRight-click: open the full launcher.",
        });
    }

    private void OnRightClick(Node _)
    {
        try
        {
            var io = ImGui.GetIO();
            if (io.KeyCtrl || io.KeyShift) return; // reserved for Umbra's quick settings
            _client?.OpenLauncher();
        }
        catch (Exception ex)
        {
            Logger.Warning("[Umbra.XivDesktop] right-click failed: " + ex.Message);
        }
    }
}
