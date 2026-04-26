using Dalamud.Configuration;

namespace Ledger.Config;

public sealed class LedgerConfig : IPluginConfiguration
{
    public int Version { get; set; } = 3;
    public bool ExcludeLimitedFromAttainable { get; set; } = true;
    public bool ExcludePremiumFromAttainable { get; set; } = true;
    public bool ExcludeRetiredPvpFromAttainable { get; set; } = true;
    public bool EnableServerComparison { get; set; }
    public string ServerBaseUrl { get; set; } = "http://localhost:5000";
}
