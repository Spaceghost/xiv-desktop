using Dalamud.Configuration;
using Dalamud.Plugin;

namespace XivDesktop.Plugin;

/// <summary>
/// Persisted by Dalamud to pluginConfigs/XivDesktop.json. Keep collection defaults empty: Newtonsoft appends
/// into pre-populated collections on load. Written only on the framework thread; lists are replaced
/// wholesale (never mutated in place) so IPC readers on other threads see a consistent reference.
/// </summary>
[Serializable]
public sealed class Configuration : IPluginConfiguration
{
    public const int CurrentVersion = 1;

    public int Version { get; set; } = CurrentVersion;

    /// <summary>Desktop-file ids, in the order they were added.</summary>
    public List<string> Favourites { get; set; } = [];

    /// <summary>Desktop-file ids, most recent first (at most UserLists.MaxRecents).</summary>
    public List<string> Recents { get; set; } = [];

    /// <summary>Linux home directory override (e.g. "/home/name"). Empty: detect it from $HOME / WINEHOMEDIR / the config path.</summary>
    public string HomeOverride { get; set; } = "";

    /// <summary>Launcher grid icon size in pixels (before global scale).</summary>
    public int IconSize { get; set; } = 48;

    /// <summary>Close the launcher window after a successful launch.</summary>
    public bool CloseOnLaunch { get; set; } = true;

    public static Configuration Load(IDalamudPluginInterface pi)
    {
        Configuration config;
        try
        {
            config = pi.GetPluginConfig() as Configuration ?? new Configuration();
        }
        catch
        {
            config = new Configuration();
        }

        config.Favourites ??= [];
        config.Recents ??= [];
        config.HomeOverride ??= "";
        config.IconSize = Math.Clamp(config.IconSize, 24, 128);
        config.Version = CurrentVersion;
        return config;
    }
}
