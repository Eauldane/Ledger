using System.Collections.Generic;

namespace Ledger.Models;

public sealed record CompareCollectionResponse(
    CompareScope Scope,
    CollectionKind CollectionKind,
    int ScopePlayerCount,
    int LoadedPlayerCount,
    IReadOnlyList<CollectableOwnershipCount> Collectables);

public sealed record CollectableOwnershipCount(
    int CollectableId,
    int OwnerCount,
    double OwnerRate);
