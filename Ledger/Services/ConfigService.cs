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

        if (config.Version < 3)
        {
            config.EnableServerComparison = false;
            if (string.IsNullOrWhiteSpace(config.ServerBaseUrl))
            {
                config.ServerBaseUrl = LedgerServerDefaults.BaseUrl;
            }

            config.Version = 3;
        }

        if (config.Version < 4)
        {
            config.ServerBaseUrl = LedgerServerDefaults.BaseUrl;
            config.Version = 4;
        }

        if (config.Version < 5)
        {
            config.MissingOnly = true;
            config.ShowExcluded = false;
            config.Version = 5;
        }
    }
}
