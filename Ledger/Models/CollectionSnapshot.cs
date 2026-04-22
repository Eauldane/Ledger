using System;
using System.Collections.Generic;
using System.Linq;

namespace Ledger.Models;

public sealed record CollectionSnapshot(
    CollectionKind Kind,
    bool OwnershipLoaded,
    string StatusMessage,
    IReadOnlyList<CollectableEntry> Entries)
{
    public int Total
        => Entries.Count;

    public int OwnedTotal
        => OwnershipLoaded ? Entries.Count(entry => entry.Owned) : 0;

    public int LimitedTotal
        => Entries.Count(entry => entry.Limited);

    public int LimitedOwned
        => OwnershipLoaded ? Entries.Count(entry => entry.Limited && entry.Owned) : 0;

    public int PremiumTotal
        => Entries.Count(entry => entry.Premium);

    public int PremiumOwned
        => OwnershipLoaded ? Entries.Count(entry => entry.Premium && entry.Owned) : 0;

    public int UnobtainableTotal
        => Entries.Count(entry => entry.Unobtainable);

    public int UnobtainableOwned
        => OwnershipLoaded ? Entries.Count(entry => entry.Unobtainable && entry.Owned) : 0;

    public int RetiredPvpTotal
        => Entries.Count(entry => entry.RetiredPvp);

    public int RetiredPvpOwned
        => OwnershipLoaded ? Entries.Count(entry => entry.RetiredPvp && entry.Owned) : 0;

    public int ExcludedTotal
        => Entries.Count(entry => entry.ExcludedFromAttainable);
    
    public int ExcludedOwned
        => OwnershipLoaded ? Entries.Count(entry => entry.ExcludedFromAttainable && entry.Owned) : 0;

    public int AttainableTotal
        => Entries.Count(entry => !entry.ExcludedFromAttainable);

    public int AttainableOwned
        => OwnershipLoaded ? Entries.Count(entry => !entry.ExcludedFromAttainable && entry.Owned) : 0;

    public int MissingAttainable
        => OwnershipLoaded ? Entries.Count(entry => !entry.ExcludedFromAttainable && !entry.Owned) : 0;

    public float CompletionRatio
        => OwnershipLoaded && AttainableTotal > 0
            ? Math.Clamp((float)AttainableOwned / AttainableTotal, 0f, 1f)
            : 0f;
}
