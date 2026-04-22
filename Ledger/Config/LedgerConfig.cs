using Dalamud.Configuration;

namespace Ledger.Config;

public sealed class LedgerConfig : IPluginConfiguration
{
    public int Version { get; set; } = 2;
    public bool ExcludeLimitedFromAttainable { get; set; } = true;
    public bool ExcludePremiumFromAttainable { get; set; } = true;
    public bool ExcludeRetiredPvpFromAttainable { get; set; } = true;
}
