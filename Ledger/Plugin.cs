using Ledger.Data;
using Ledger.Services;
using Ledger.UI;
using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace Ledger;

public sealed class Plugin : IDalamudPlugin
{
    private readonly WindowSystem _windowSystem = new("Ledger");
    private readonly IDalamudPluginInterface _pluginInterface;
    private readonly ICommandManager _commandManager;

    private readonly ConfigService _config;
    private readonly CollectionDataService _collections;
    private readonly LedgerWindow _window;

    public Plugin(
        IDalamudPluginInterface pluginInterface,
        ICommandManager commandManager,
        IDataManager dataManager,
        IUnlockState unlockState,
        IClientState clientState)
    {
        _pluginInterface = pluginInterface;
        _commandManager = commandManager;

        LimitedCollectables.Initialize(pluginInterface.AssemblyLocation.DirectoryName!);

        _config = new ConfigService(pluginInterface);
        _collections = new CollectionDataService(dataManager, unlockState, clientState, _config);
        _window = new LedgerWindow(_collections, _config);
        _windowSystem.AddWindow(_window);

        _commandManager.AddHandler("/ledger", new CommandInfo(OnCommand)
        {
            HelpMessage = "Open Ledger.",
        });

        _pluginInterface.UiBuilder.Draw += DrawUi;
        _pluginInterface.UiBuilder.OpenConfigUi += OpenUi;
        _pluginInterface.UiBuilder.OpenMainUi += OpenUi;
    }

    public void Dispose()
    {
        _pluginInterface.UiBuilder.Draw -= DrawUi;
        _pluginInterface.UiBuilder.OpenConfigUi -= OpenUi;
        _pluginInterface.UiBuilder.OpenMainUi -= OpenUi;
        _commandManager.RemoveHandler("/ledger");
        _windowSystem.RemoveAllWindows();
    }

    private void OnCommand(string command, string arguments)
        => OpenUi();

    private void OpenUi()
        => _window.IsOpen = true;

    private void DrawUi()
        => _windowSystem.Draw();
}
