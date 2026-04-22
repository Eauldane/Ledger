namespace Ledger.Models;

public sealed record CollectableEntry(
    CollectionKind Kind,
    uint Id,
    string Name,
    string Detail,
    bool Owned,
    bool Limited,
    bool Premium,
    bool Unobtainable,
    bool RetiredPvp,
    bool ExcludedFromAttainable);
