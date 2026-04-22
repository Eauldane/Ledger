using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Ledger.Models;

namespace Ledger.Data;

public static class LimitedCollectables
{
    private const string FileName = "limited-collectables.json";

    private static readonly object SyncRoot = new();
    private static string _pluginDirectory = string.Empty;
    private static string _loadedPath = string.Empty;
    private static string _loadError = string.Empty;
    private static bool _initialized;

    public static IReadOnlySet<uint> LimitedAchievements { get; private set; } = new HashSet<uint>();
    public static IReadOnlySet<uint> LimitedMinions { get; private set; } = new HashSet<uint>();
    public static IReadOnlySet<uint> LimitedTitles { get; private set; } = new HashSet<uint>();
    public static IReadOnlySet<uint> LimitedTripleTriadCards { get; private set; } = new HashSet<uint>();
    public static IReadOnlySet<uint> LimitedMounts { get; private set; } = new HashSet<uint>();
    public static IReadOnlySet<uint> LimitedOrchestrionRolls { get; private set; } = new HashSet<uint>();
    public static IReadOnlySet<uint> LimitedFashionAccessories { get; private set; } = new HashSet<uint>();
    public static IReadOnlySet<uint> LimitedFacewear { get; private set; } = new HashSet<uint>();

    public static IReadOnlySet<uint> PremiumAchievements { get; private set; } = new HashSet<uint>();
    public static IReadOnlySet<uint> PremiumMinions { get; private set; } = new HashSet<uint>();
    public static IReadOnlySet<uint> PremiumTitles { get; private set; } = new HashSet<uint>();
    public static IReadOnlySet<uint> PremiumTripleTriadCards { get; private set; } = new HashSet<uint>();
    public static IReadOnlySet<uint> PremiumMounts { get; private set; } = new HashSet<uint>();
    public static IReadOnlySet<uint> PremiumOrchestrionRolls { get; private set; } = new HashSet<uint>();
    public static IReadOnlySet<uint> PremiumFashionAccessories { get; private set; } = new HashSet<uint>();
    public static IReadOnlySet<uint> PremiumFacewear { get; private set; } = new HashSet<uint>();

    public static IReadOnlySet<uint> UnobtainableAchievements { get; private set; } = new HashSet<uint>();
    public static IReadOnlySet<uint> UnobtainableMinions { get; private set; } = new HashSet<uint>();
    public static IReadOnlySet<uint> UnobtainableTitles { get; private set; } = new HashSet<uint>();
    public static IReadOnlySet<uint> UnobtainableTripleTriadCards { get; private set; } = new HashSet<uint>();
    public static IReadOnlySet<uint> UnobtainableMounts { get; private set; } = new HashSet<uint>();
    public static IReadOnlySet<uint> UnobtainableOrchestrionRolls { get; private set; } = new HashSet<uint>();
    public static IReadOnlySet<uint> UnobtainableFashionAccessories { get; private set; } = new HashSet<uint>();
    public static IReadOnlySet<uint> UnobtainableFacewear { get; private set; } = new HashSet<uint>();

    public static IReadOnlySet<uint> RetiredPvpAchievements { get; private set; } = new HashSet<uint>();
    public static IReadOnlySet<uint> RetiredPvpMinions { get; private set; } = new HashSet<uint>();
    public static IReadOnlySet<uint> RetiredPvpTitles { get; private set; } = new HashSet<uint>();
    public static IReadOnlySet<uint> RetiredPvpTripleTriadCards { get; private set; } = new HashSet<uint>();
    public static IReadOnlySet<uint> RetiredPvpMounts { get; private set; } = new HashSet<uint>();
    public static IReadOnlySet<uint> RetiredPvpOrchestrionRolls { get; private set; } = new HashSet<uint>();
    public static IReadOnlySet<uint> RetiredPvpFashionAccessories { get; private set; } = new HashSet<uint>();
    public static IReadOnlySet<uint> RetiredPvpFacewear { get; private set; } = new HashSet<uint>();

    public static bool IsLoaded
    {
        get
        {
            EnsureInitialized();
            return !string.IsNullOrWhiteSpace(_loadedPath);
        }
    }

    public static string LoadedPath
    {
        get
        {
            EnsureInitialized();
            return _loadedPath;
        }
    }

    public static string LoadError
    {
        get
        {
            EnsureInitialized();
            return _loadError;
        }
    }

    public static void Initialize(string pluginDirectory)
    {
        lock (SyncRoot)
        {
            _pluginDirectory = NormalizeBaseDirectory(pluginDirectory);
            ApplyFile(LoadFile());
            _initialized = true;
        }
    }

    public static CollectableExclusionIds GetIds(CollectionKind kind)
    {
        EnsureInitialized();
        return kind switch
        {
            CollectionKind.Achievements => new CollectableExclusionIds(LimitedAchievements, PremiumAchievements, UnobtainableAchievements, RetiredPvpAchievements),
            CollectionKind.Minions => new CollectableExclusionIds(LimitedMinions, PremiumMinions, UnobtainableMinions, RetiredPvpMinions),
            CollectionKind.Titles => new CollectableExclusionIds(LimitedTitles, PremiumTitles, UnobtainableTitles, RetiredPvpTitles),
            CollectionKind.TripleTriadCards => new CollectableExclusionIds(LimitedTripleTriadCards, PremiumTripleTriadCards, UnobtainableTripleTriadCards, RetiredPvpTripleTriadCards),
            CollectionKind.Mounts => new CollectableExclusionIds(LimitedMounts, PremiumMounts, UnobtainableMounts, RetiredPvpMounts),
            CollectionKind.OrchestrionRolls => new CollectableExclusionIds(LimitedOrchestrionRolls, PremiumOrchestrionRolls, UnobtainableOrchestrionRolls, RetiredPvpOrchestrionRolls),
            CollectionKind.FashionAccessories => new CollectableExclusionIds(LimitedFashionAccessories, PremiumFashionAccessories, UnobtainableFashionAccessories, RetiredPvpFashionAccessories),
            CollectionKind.Facewear => new CollectableExclusionIds(LimitedFacewear, PremiumFacewear, UnobtainableFacewear, RetiredPvpFacewear),
            _ => new CollectableExclusionIds(LimitedAchievements, PremiumAchievements, UnobtainableAchievements, RetiredPvpAchievements),
        };
    }

    private static IReadOnlySet<uint> ToSet(IEnumerable<uint>? ids)
        => ids is null ? new HashSet<uint>() : ids.ToHashSet();

    private static LimitedCollectablesFile LoadFile()
    {
        var candidatePaths = CandidatePaths().ToArray();
        string? firstError = null;
        _loadedPath = string.Empty;

        foreach (var path in candidatePaths)
        {
            if (!File.Exists(path))
            {
                continue;
            }

            try
            {
                using var stream = File.OpenRead(path);
                var loaded = JsonSerializer.Deserialize(stream, LimitedCollectablesJsonContext.Default.LimitedCollectablesFile);
                if (loaded is not null)
                {
                    _loadedPath = path;
                    _loadError = string.Empty;
                    return loaded;
                }

                firstError ??= $"{path}: empty or invalid JSON.";
            }
            catch (Exception ex)
            {
                firstError ??= $"{path}: {ex.Message}";
            }
        }

        var checkedPaths = string.Join("; ", candidatePaths);
        _loadError = firstError is null
            ? $"Could not find {FileName}. Checked: {checkedPaths}"
            : $"Could not load {FileName}. {firstError}. Checked: {checkedPaths}";
        return new LimitedCollectablesFile();
    }

    private static void EnsureInitialized()
    {
        if (_initialized)
        {
            return;
        }

        lock (SyncRoot)
        {
            if (_initialized)
            {
                return;
            }

            ApplyFile(LoadFile());
            _initialized = true;
        }
    }

    private static void ApplyFile(LimitedCollectablesFile file)
    {
        var limited = file.Limited ?? file.ToLegacyLimitedLists();
        var premium = file.Premium ?? new CollectableIdLists();
        var unobtainable = file.Unobtainable ?? new CollectableIdLists();
        var retiredPvp = file.RetiredPvp ?? new CollectableIdLists();

        LimitedAchievements = ToSet(limited.Achievements);
        LimitedMinions = ToSet(limited.Minions);
        LimitedTitles = ToSet(limited.Titles);
        LimitedTripleTriadCards = ToSet(limited.TripleTriadCards);
        LimitedMounts = ToSet(limited.Mounts);
        LimitedOrchestrionRolls = ToSet(limited.OrchestrionRolls);
        LimitedFashionAccessories = ToSet(limited.FashionAccessories);
        LimitedFacewear = ToSet(limited.Facewear);

        PremiumAchievements = ToSet(premium.Achievements);
        PremiumMinions = ToSet(premium.Minions);
        PremiumTitles = ToSet(premium.Titles);
        PremiumTripleTriadCards = ToSet(premium.TripleTriadCards);
        PremiumMounts = ToSet(premium.Mounts);
        PremiumOrchestrionRolls = ToSet(premium.OrchestrionRolls);
        PremiumFashionAccessories = ToSet(premium.FashionAccessories);
        PremiumFacewear = ToSet(premium.Facewear);

        UnobtainableAchievements = ToSet(unobtainable.Achievements);
        UnobtainableMinions = ToSet(unobtainable.Minions);
        UnobtainableTitles = ToSet(unobtainable.Titles);
        UnobtainableTripleTriadCards = ToSet(unobtainable.TripleTriadCards);
        UnobtainableMounts = ToSet(unobtainable.Mounts);
        UnobtainableOrchestrionRolls = ToSet(unobtainable.OrchestrionRolls);
        UnobtainableFashionAccessories = ToSet(unobtainable.FashionAccessories);
        UnobtainableFacewear = ToSet(unobtainable.Facewear);

        RetiredPvpAchievements = ToSet(retiredPvp.Achievements);
        RetiredPvpMinions = ToSet(retiredPvp.Minions);
        RetiredPvpTitles = ToSet(retiredPvp.Titles);
        RetiredPvpTripleTriadCards = ToSet(retiredPvp.TripleTriadCards);
        RetiredPvpMounts = ToSet(retiredPvp.Mounts);
        RetiredPvpOrchestrionRolls = ToSet(retiredPvp.OrchestrionRolls);
        RetiredPvpFashionAccessories = ToSet(retiredPvp.FashionAccessories);
        RetiredPvpFacewear = ToSet(retiredPvp.Facewear);
    }

    private static IEnumerable<string> CandidatePaths()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var baseDirectory in BaseDirectories())
        {
            foreach (var path in new[]
            {
                Path.Combine(baseDirectory, "Data", FileName),
                Path.Combine(baseDirectory, FileName),
            })
            {
                if (seen.Add(path))
                {
                    yield return path;
                }
            }
        }
    }

    private static IEnumerable<string> BaseDirectories()
    {
        if (!string.IsNullOrWhiteSpace(_pluginDirectory))
        {
            yield return _pluginDirectory;
        }

        var assemblyDirectory = Path.GetDirectoryName(typeof(LimitedCollectables).Assembly.Location);
        if (!string.IsNullOrWhiteSpace(assemblyDirectory))
        {
            yield return assemblyDirectory;
        }

        yield return AppContext.BaseDirectory;
    }

    private static string NormalizeBaseDirectory(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        return File.Exists(path)
            ? Path.GetDirectoryName(path) ?? string.Empty
            : path;
    }
}

public sealed record LimitedCollectablesFile
{
    public int SchemaVersion { get; init; } = 4;
    public DateTimeOffset? GeneratedAtUtc { get; init; }

    public CollectableIdLists? Limited { get; init; }
    public CollectableIdLists? Premium { get; init; }
    public CollectableIdLists? Unobtainable { get; init; }
    public CollectableIdLists? RetiredPvp { get; init; }

    public uint[] Achievements { get; init; } = [];
    public uint[] Minions { get; init; } = [];
    public uint[] Titles { get; init; } = [];
    public uint[] TripleTriadCards { get; init; } = [];
    public uint[] Mounts { get; init; } = [];
    public uint[] OrchestrionRolls { get; init; } = [];
    public uint[] FashionAccessories { get; init; } = [];
    public uint[] Facewear { get; init; } = [];

    public CollectableIdLists ToLegacyLimitedLists()
        => new()
        {
            Achievements = Achievements,
            Minions = Minions,
            Titles = Titles,
            TripleTriadCards = TripleTriadCards,
            Mounts = Mounts,
            OrchestrionRolls = OrchestrionRolls,
            FashionAccessories = FashionAccessories,
            Facewear = Facewear,
        };
}

public sealed record CollectableIdLists
{
    public uint[] Achievements { get; init; } = [];
    public uint[] Minions { get; init; } = [];
    public uint[] Titles { get; init; } = [];
    public uint[] TripleTriadCards { get; init; } = [];
    public uint[] Mounts { get; init; } = [];
    public uint[] OrchestrionRolls { get; init; } = [];
    public uint[] FashionAccessories { get; init; } = [];
    public uint[] Facewear { get; init; } = [];
}

public sealed record CollectableExclusionIds(
    IReadOnlySet<uint> Limited,
    IReadOnlySet<uint> Premium,
    IReadOnlySet<uint> Unobtainable,
    IReadOnlySet<uint> RetiredPvp);

[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(LimitedCollectablesFile))]
[JsonSerializable(typeof(CollectableIdLists))]
internal sealed partial class LimitedCollectablesJsonContext : JsonSerializerContext;
