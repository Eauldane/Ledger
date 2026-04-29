using Ledger.Data;
using Ledger.Services;
using Ledger.UI;
using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using ElezenTools;

namespace Ledger;

public sealed class Plugin : IDalamudPlugin
{
    private readonly WindowSystem _windowSystem = new("Ledger");
    private readonly IDalamudPluginInterface _pluginInterface;
    private readonly ICommandManager _commandManager;

    private readonly ConfigService _config;
    private readonly CollectionDataService _collections;
    private readonly TitleTrackingService _titles;
    private readonly PlayerIdentityService _playerIdentity;
    private readonly LedgerServerService _server;
    private readonly LedgerWindow _mainWindow;
    private readonly LedgerSettingsWindow _settingsWindow;

    public Plugin(
        IDalamudPluginInterface pluginInterface,
        ICommandManager commandManager,
        IAddonLifecycle addonLifecycle,
        IClientState clientState,
        IDataManager dataManager,
        IPlayerState playerState,
        IObjectTable objectTable,
        IUnlockState unlockState,
        IFramework framework)
    {
        _pluginInterface = pluginInterface;
        _commandManager = commandManager;
        ElezenInit.Init(pluginInterface, this);

        LimitedCollectables.Initialize(pluginInterface.AssemblyLocation.DirectoryName!);

        _config = new ConfigService(pluginInterface);
        _titles = new TitleTrackingService(addonLifecycle, clientState, dataManager, unlockState);
        _collections = new CollectionDataService(clientState, _config, _titles);
        _titles.Changed += _collections.Invalidate;
        _playerIdentity = new PlayerIdentityService(framework, playerState, objectTable);
        _server = new LedgerServerService(_collections, _config, _playerIdentity, framework);
        _settingsWindow = new LedgerSettingsWindow(_collections, _config, _server);
        _mainWindow = new LedgerWindow(_collections, _config, _server, _playerIdentity, OpenSettingsUi);
        _windowSystem.AddWindow(_mainWindow);
        _windowSystem.AddWindow(_settingsWindow);

        _commandManager.AddHandler("/ledger", new CommandInfo(OnCommand)
        {
            HelpMessage = "Open Ledger.",
        });

        _pluginInterface.UiBuilder.Draw += DrawUi;
        _pluginInterface.UiBuilder.OpenConfigUi += OpenSettingsUi;
        _pluginInterface.UiBuilder.OpenMainUi += OpenMainUi;
    }

    public void Dispose()
    {
        _pluginInterface.UiBuilder.Draw -= DrawUi;
        _pluginInterface.UiBuilder.OpenConfigUi -= OpenSettingsUi;
        _pluginInterface.UiBuilder.OpenMainUi -= OpenMainUi;
        _commandManager.RemoveHandler("/ledger");
        _windowSystem.RemoveAllWindows();
        _titles.Changed -= _collections.Invalidate;
        _titles.Dispose();
        _server.Dispose();
        ElezenInit.Dispose();
    }

    private void OnCommand(string command, string arguments)
        => OpenMainUi();

    private void OpenMainUi()
        => _mainWindow.IsOpen = true;

    private void OpenSettingsUi()
        => _settingsWindow.IsOpen = true;

    private void DrawUi()
        => _windowSystem.Draw();
}
