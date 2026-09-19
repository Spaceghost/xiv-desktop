using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Services;
using KamiToolKit;
using XivDesktop.Core;
using XivDesktop.Shared;

namespace XivDesktop.Plugin.Ask;

/// <summary>
/// Wires "Ask an NPC" into the plugin: /ask, the XivDesktop.v1.Ask gate, the backend, the speaker sheets,
/// the service, and the UI (KamiToolKit's native addons, or the ImGui fallback when KamiToolKit fails to
/// start). Construct and dispose on the framework thread.
/// </summary>
public sealed class AskModule : IDisposable
{
    /// <summary>XivDesktop's own command. ghostty-dalamud owns "/ask" (its glass chat panel).</summary>
    public const string NpcCommand = "/npc";

    /// <summary>Only registered when "Route /ask to the NPC" is on and no other plugin has taken it.</summary>
    public const string AskCommand = "/ask";

    private readonly IDalamudPluginInterface pi;
    private readonly IFramework framework;
    private readonly ICommandManager commands;
    private readonly IChatGui chat;
    private readonly IPluginLog log;
    private readonly WindowSystem windows;
    private readonly ITextureProvider textures;
    private readonly Configuration config;
    private readonly IObjectTable objects;
    private readonly AskBackend backend;
    private readonly ICallGateProvider<string, string> gate;
    private NativeTalkView? nativeView;
    private SpeakerPickerAddon? picker;
    private ImGuiTalkWindow? fallback;
    private bool kamiReady;
    private bool commandRegistered;
    private bool askRegistered;
    private bool disposed;

    public AskModule(IDalamudPluginInterface pi, IFramework framework, ICommandManager commands, IChatGui chat, IPluginLog log,
        IDataManager data, IClientState clientState, ICondition condition, IObjectTable objects, ITextureProvider textures,
        WindowSystem windows, Configuration config, Func<bool> terminalAvailable, Action<string> postTerminal)
    {
        this.pi = pi;
        this.framework = framework;
        this.commands = commands;
        this.chat = chat;
        this.log = log;
        this.windows = windows;
        this.textures = textures;
        this.config = config;
        this.objects = objects;

        var configDir = pi.GetPluginConfigDirectory();
        backend = new AskBackend(log, () =>
        {
            var home = config.HomeOverride;
            if (string.IsNullOrWhiteSpace(home))
                home = HostPaths.GuessHome(Environment.GetEnvironmentVariable("HOME"), Environment.GetEnvironmentVariable("WINEHOMEDIR"), configDir);
            return OperatingSystem.IsWindows() ? HostPaths.Wine(home) : HostPaths.Native(home);
        });
        Sheets = new SpeakerSheets(data, framework, log);
        Service = new AskService(config, () => pi.SavePluginConfig(config), framework, clientState, condition, objects, log, backend, Sheets, terminalAvailable, postTerminal);
        Sheets.Changed += () => picker?.Refresh();

        commandRegistered = commands.AddHandler(NpcCommand, new CommandInfo((_, a) => Run(a))
        {
            HelpMessage = "Ask an NPC: \"/npc <question>\" summons a speaker and asks; \"/npc\" summons it; \"/npc bye\" dismisses; \"/npc as <scholar|moogle|archivist|myself|search>\" picks who; \"/npc who\" opens the picker.",
        });
        if (!commandRegistered)
            log.Warning("XivDesktop ask: {Command} is taken by another plugin; use \"/desktop ask …\"", NpcCommand);

        // ghostty-dalamud registers /ask for its own chat panel. Taking it away from a loaded plugin is not
        // possible (Dalamud gives the command to whoever registered first, and there is no hand-over IPC), so
        // the setting only claims /ask when nothing else holds it; otherwise it says so and /npc stays.
        if (config.AskRouteSlashAsk)
        {
            askRegistered = commands.AddHandler(AskCommand, new CommandInfo((_, a) => Run(a))
            {
                HelpMessage = "Ask an NPC (routed from XivDesktop settings); \"/npc\" does the same.",
            });
            if (!askRegistered)
                log.Information("XivDesktop ask: /ask is owned by another plugin (ghostty-dalamud's chat panel); use /npc");
        }

        gate = pi.GetIpcProvider<string, string>(IpcContract.Ask);
        gate.RegisterFunc(text =>
        {
            try
            {
                var t = framework.RunOnFrameworkThread(() => Run(text ?? "", print: false));
                return t.Wait(TimeSpan.FromSeconds(2)) ? t.Result : "error: timed out waiting for the framework thread";
            }
            catch (Exception ex)
            {
                log.Warning(ex, "XivDesktop ask: IPC call failed");
                return "error: internal error";
            }
        });

        _ = StartUi();
    }

    public AskService Service { get; }

    public SpeakerSheets Sheets { get; }

    /// <summary>The UI technology in use, for settings: "KamiToolKit", "ImGui fallback (…)", or "starting".</summary>
    public string UiNote { get; private set; } = "starting";

    private async Task StartUi()
    {
        try
        {
            await KamiToolKitLibrary.InitializeAsync(pi).ConfigureAwait(false);
            await framework.RunOnFrameworkThread(() =>
            {
                if (disposed)
                    return;
                nativeView = new NativeTalkView();
                Service.Attach(nativeView);
                kamiReady = true;
                UiNote = "KamiToolKit (native addons)";
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            log.Warning(ex, "XivDesktop ask: KamiToolKit could not start; using the ImGui dialogue with the game's Talk texture");
            await framework.RunOnFrameworkThread(() =>
            {
                if (disposed)
                    return;
                fallback = new ImGuiTalkWindow(textures);
                windows.AddWindow(fallback);
                Service.Attach(fallback);
                UiNote = $"ImGui fallback ({ex.GetType().Name})";
            }).ConfigureAwait(false);
        }
    }

    /// <summary>Whether the optional /ask route is actually held by this plugin.</summary>
    public bool OwnsSlashAsk => askRegistered;

    /// <summary>/npc, /desktop ask (and /ask when the setting claimed it). Returns the status line (also printed to chat when <paramref name="print"/>).</summary>
    public string Run(string args, bool print = true)
    {
        string result;
        try
        {
            result = Service.Command(args);
            if (result.StartsWith("ok: picker", StringComparison.Ordinal))
                result = OpenPicker(result.Length > 10 ? result[11..] : "");
        }
        catch (Exception ex)
        {
            log.Error(ex, "XivDesktop ask failed");
            result = "error: internal error (see /xllog)";
        }

        if (print && result.StartsWith("error", StringComparison.Ordinal))
            Print(result[7..]);
        return result;
    }

    /// <summary>Opens the native speaker picker (or the settings tab when KamiToolKit is unavailable).</summary>
    public string OpenPicker(string query = "")
    {
        Sheets.Refresh(objects.LocalPlayer?.Name.TextValue ?? "");
        if (!kamiReady)
            return "error: the in-game picker needs KamiToolKit; use /desktop settings → Ask";
        picker ??= CreatePicker();
        if (!picker.IsOpen)
            picker.Open();
        picker.SetSearch(query);
        return "ok: picker open";
    }

    private SpeakerPickerAddon CreatePicker()
    {
        var p = SpeakerPickerAddon.Create();
        p.Catalog = () => Sheets.Catalog;
        p.Favourites = () => config.AskFavourites;
        p.Recents = () => config.AskRecents;
        p.Current = () => string.IsNullOrWhiteSpace(config.AskSpeaker) ? Core.Ask.SpeakerPresets.DefaultKey : config.AskSpeaker;
        p.Chosen = e => Print(Service.SetSpeaker(e.Key)[4..]);
        p.ToggleFavourite = e => Service.ToggleFavourite(e);
        p.Preview = e =>
        {
            var r = Service.Preview(e);
            if (r.StartsWith("error", StringComparison.Ordinal))
                Print(r[7..]);
        };
        return p;
    }

    private void Print(string message)
    {
        try
        {
            chat.Print(message, "Ask");
        }
        catch
        {
            // Chat unavailable (title screen).
        }
    }

    public void Dispose()
    {
        disposed = true;
        if (commandRegistered)
        {
            commands.RemoveHandler(NpcCommand);
            commandRegistered = false;
        }

        if (askRegistered)
        {
            commands.RemoveHandler(AskCommand);
            askRegistered = false;
        }

        gate.UnregisterFunc();
        Service.Dispose();
        backend.Dispose();
        if (fallback is not null)
            windows.RemoveWindow(fallback);
        try
        {
            picker?.Dispose();
            nativeView?.Dispose();
            if (kamiReady)
                KamiToolKitLibrary.Dispose();
        }
        catch (Exception ex)
        {
            log.Warning(ex, "XivDesktop ask: disposing the native UI failed");
        }
    }
}
