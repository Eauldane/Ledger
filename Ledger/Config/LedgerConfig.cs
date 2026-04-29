using Dalamud.Configuration;

namespace Ledger.Config;

public sealed class LedgerConfig : IPluginConfiguration
{
    public int Version { get; set; } = 5;
    public bool ExcludeLimitedFromAttainable { get; set; } = true;
    public bool ExcludePremiumFromAttainable { get; set; } = true;
    public bool ExcludeRetiredPvpFromAttainable { get; set; } = true;
    public bool EnableServerComparison { get; set; } = false;
    public bool MissingOnly { get; set; } = true;
    public bool ShowExcluded { get; set; }
    public string ServerBaseUrl { get; set; } = LedgerServerDefaults.BaseUrl;
}
