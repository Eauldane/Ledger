using System;
using System.Collections.Generic;

namespace Ledger.Models;

public sealed record SyncPlayerRequest
{
    public string Ident { get; init; } = string.Empty;
    public int HomeWorldId { get; init; }
    public int DatacenterId { get; init; }
    public int RegionId { get; init; }
    public IReadOnlyList<SyncCollectionRequest> Collections { get; init; } = [];
}

public sealed record SyncCollectionRequest
{
    public CollectionKind Kind { get; init; }
    public bool OwnershipLoaded { get; init; }
    public IReadOnlyList<int> OwnedIds { get; init; } = [];
}

public sealed record SyncFriendsRequest
{
    public IReadOnlyList<string> FriendIdents { get; init; } = [];
}

public sealed record SyncPlayerResponse(
    string Ident,
    long PlayerId,
    int CollectionCount,
    int UnlockCount,
    DateTimeOffset SyncedAt);

public sealed record SyncFriendsResponse(
    string Ident,
    int FriendCount,
    int ResolvedFriendCount,
    DateTimeOffset SyncedAt);
