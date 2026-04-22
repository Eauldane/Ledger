namespace Ledger.Models;

public enum CollectionKind
{
    Achievements,
    Minions,
    Titles,
    TripleTriadCards,
    Mounts,
    OrchestrionRolls,
    FashionAccessories,
    Facewear,
}

public static class CollectionKindExtensions
{
    public static readonly CollectionKind[] All =
    [
        CollectionKind.Achievements,
        CollectionKind.Minions,
        CollectionKind.Titles,
        CollectionKind.TripleTriadCards,
        CollectionKind.Mounts,
        CollectionKind.OrchestrionRolls,
        CollectionKind.FashionAccessories,
        CollectionKind.Facewear,
    ];

    public static string ToDisplayName(this CollectionKind kind)
        => kind switch
        {
            CollectionKind.Achievements => "Achievements",
            CollectionKind.Minions => "Minions",
            CollectionKind.Titles => "Titles",
            CollectionKind.TripleTriadCards => "Triple Triad Cards",
            CollectionKind.Mounts => "Mounts",
            CollectionKind.OrchestrionRolls => "Orchestrion Rolls",
            CollectionKind.FashionAccessories => "Fashion Accessories",
            CollectionKind.Facewear => "Facewear",
            _ => kind.ToString(),
        };
}
