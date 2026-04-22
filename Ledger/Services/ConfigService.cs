using Dalamud.Plugin;
using Ledger.Config;

namespace Ledger.Services;

public sealed class ConfigService
{
    private readonly IDalamudPluginInterface _pluginInterface;

    public ConfigService(IDalamudPluginInterface pluginInterface)
    {
        _pluginInterface = pluginInterface;
        Current = pluginInterface.GetPluginConfig() as LedgerConfig ?? new LedgerConfig();
        Migrate(Current);
        Save();
    }

    public LedgerConfig Current { get; }

    public void Save()
        => _pluginInterface.SavePluginConfig(Current);

    private static void Migrate(LedgerConfig config)
    {
        if (config.Version < 2)
        {
            config.ExcludeLimitedFromAttainable = true;
            config.Version = 2;
        }
    }
}
