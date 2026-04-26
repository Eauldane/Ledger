using System.Collections.Generic;

namespace Ledger.Models;

public sealed record CompareCollectionResponse(
    CompareScope Scope,
    string RequesterIdent,
    CollectionKind CollectionKind,
    int ScopePlayerCount,
    int LoadedPlayerCount,
    int? RequesterOwnedCount,
    int? RequesterRank,
    double? RequesterPercentile,
    double? AverageOwnedCount,
    double? MedianOwnedCount,
    IReadOnlyList<ComparePlayerStanding> TopPlayers,
    IReadOnlyList<CollectableOwnershipCount> Collectables);

public sealed record ComparePlayerStanding(
    string Ident,
    string? CharacterName,
    int HomeWorldId,
    int OwnedCount,
    int Rank);

public sealed record CollectableOwnershipCount(
    int CollectableId,
    int OwnerCount,
    double OwnerRate);

public sealed record CompareOwnersResponse(
    CompareScope Scope,
    string RequesterIdent,
    CollectionKind CollectionKind,
    int CollectableId,
    int OwnerCount,
    IReadOnlyList<CollectableOwner> Owners);

public sealed record CollectableOwner(
    string Ident,
    string? CharacterName,
    int HomeWorldId,
    int DatacenterId,
    int RegionId);
