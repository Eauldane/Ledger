namespace Ledger.Models;

public enum CompareScope
{
    All,
    Server,
    Datacentre,
    Region,
    Friends,
}

public static class CompareScopeExtensions
{
    public static readonly CompareScope[] AllScopes =
    [
        CompareScope.All,
        CompareScope.Server,
        CompareScope.Datacentre,
        CompareScope.Region,
        CompareScope.Friends,
    ];

    public static string ToDisplayName(this CompareScope scope)
        => scope switch
        {
            CompareScope.All => "All Players",
            CompareScope.Server => "Server",
            CompareScope.Datacentre => "Datacentre",
            CompareScope.Region => "Region",
            CompareScope.Friends => "Friends",
            _ => scope.ToString(),
        };

    public static string ToApiValue(this CompareScope scope)
        => scope switch
        {
            CompareScope.All => "all",
            CompareScope.Server => "server",
            CompareScope.Datacentre => "datacentre",
            CompareScope.Region => "region",
            CompareScope.Friends => "friends",
            _ => scope.ToString().ToLowerInvariant(),
        };
}
