using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace GlamourSync;

public sealed class Plugin : IDalamudPlugin
{
    private const string CommandName = "/glamoursync";
    private void OpenConfig() => mainWindow.IsOpen = true;

    [PluginService] private static IDalamudPluginInterface PluginInterface { get; set; } = null!;
    [PluginService] private static ICommandManager CommandManager { get; set; } = null!;
    [PluginService] private static IDataManager DataManager { get; set; } = null!;
    [PluginService] private static IPluginLog Log { get; set; } = null!;
    [PluginService] private static ITextureProvider TextureProvider { get; set; } = null!;
    [PluginService] private static IChatGui ChatGui { get; set; } = null!;

    private readonly WindowSystem windowSystem = new("GlamourSync");
    private readonly MainWindow mainWindow;

    public Plugin()
    {
        var config = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        config.Initialize(PluginInterface);

        var resolver = new ItemResolver(DataManager, Log);
        var glamourerBridge = new GlamourerBridge(PluginInterface);

        mainWindow = new MainWindow(resolver, glamourerBridge, TextureProvider, config);
        mainWindow.Notify = msg => ChatGui.Print(msg, "GlamourSync");
        windowSystem.AddWindow(mainWindow);

        CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Open GlamourSync.\n"
                + "/glamoursync apply - read the clipboard and apply it right away\n"
                + "/glamoursync revert - go back to your previous look"
        });

        PluginInterface.UiBuilder.Draw += windowSystem.Draw;
        PluginInterface.UiBuilder.OpenMainUi += mainWindow.Toggle;
        PluginInterface.UiBuilder.OpenConfigUi += OpenConfig;
    }

    private void OnCommand(string command, string args)
    {
        if (string.IsNullOrWhiteSpace(args)) mainWindow.Toggle();
        else mainWindow.HandleCommand(args);
    }

    public void Dispose()
    {
        PluginInterface.UiBuilder.Draw -= windowSystem.Draw;
        PluginInterface.UiBuilder.OpenMainUi -= mainWindow.Toggle;
        PluginInterface.UiBuilder.OpenConfigUi -= OpenConfig;
        windowSystem.RemoveAllWindows();
        CommandManager.RemoveHandler(CommandName);

        mainWindow.Dispose();
    }
}