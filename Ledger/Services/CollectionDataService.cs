using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Plugin.Services;
using ElezenTools.Data;
using ElezenTools.Data.Classes;
using Ledger.Data;
using Ledger.Models;

namespace Ledger.Services;

public sealed class CollectionDataService
{
    private static readonly TimeSpan CacheDuration = TimeSpan.FromSeconds(2);

    private readonly IClientState _clientState;
    private readonly ConfigService _config;
    private readonly TitleTrackingService _titles;
    private readonly object _cacheSync = new();
    private readonly Dictionary<CollectionKind, CollectionSnapshot> _snapshotCache = new();
    private AttainableSettings _cachedSettings;
    private DateTime _cacheExpiresUtc;

    public CollectionDataService(IClientState clientState, ConfigService config, TitleTrackingService titles)
    {
        _clientState = clientState;
        _config = config;
        _titles = titles;
    }

    public void Invalidate()
    {
        lock (_cacheSync)
        {
            _snapshotCache.Clear();
            _cacheExpiresUtc = DateTime.MinValue;
        }
    }

    public CollectionSnapshot GetSnapshot(CollectionKind kind)
    {
        lock (_cacheSync)
        {
            var now = DateTime.UtcNow;
            var settings = GetAttainableSettings();
            if (now >= _cacheExpiresUtc || settings != _cachedSettings)
            {
                _snapshotCache.Clear();
                _cachedSettings = settings;
                _cacheExpiresUtc = now + CacheDuration;
            }

            if (_snapshotCache.TryGetValue(kind, out var snapshot))
            {
                return snapshot;
            }

            snapshot = kind switch
            {
                CollectionKind.Achievements => BuildAchievementSnapshot(),
                CollectionKind.Minions => BuildMinionSnapshot(),
                CollectionKind.Titles => BuildTitleSnapshot(),
                CollectionKind.TripleTriadCards => BuildTripleTriadCardSnapshot(),
                CollectionKind.Mounts => BuildMountSnapshot(),
                CollectionKind.OrchestrionRolls => BuildOrchestrionSnapshot(),
                CollectionKind.FashionAccessories => BuildFashionAccessorySnapshot(),
                CollectionKind.Facewear => BuildFacewearSnapshot(),
                _ => new CollectionSnapshot(kind, false, "Unknown collection type.", []),
            };

            _snapshotCache[kind] = snapshot;
            return snapshot;
        }
    }

    public SyncCollectionRequest[] BuildSyncCollections(bool loadedOnly = false)
    {
        var collections = new List<SyncCollectionRequest>(CollectionKindExtensions.All.Length);
        foreach (var kind in CollectionKindExtensions.All)
        {
            var snapshot = GetSnapshot(kind);
            if (loadedOnly && !snapshot.OwnershipLoaded)
            {
                continue;
            }

            collections.Add(new SyncCollectionRequest
            {
                Kind = snapshot.Kind,
                OwnershipLoaded = snapshot.OwnershipLoaded,
                OwnedIds = snapshot.OwnershipLoaded
                    ? snapshot.Entries.Where(entry => entry.Owned).Select(entry => (int)entry.Id).ToArray()
                    : [],
            });
        }

        return [.. collections];
    }

    private CollectionSnapshot BuildAchievementSnapshot()
    {
        var loaded = ElezenData.Achievements.IsUnlockListLoaded;
        var ownedIds = ToOwnedIds(loaded, ElezenData.Achievements.GetUnlocked(), item => item.Id);
        return BuildSnapshot(
            CollectionKind.Achievements,
            loaded,
            "Open the achievements window to load your achievements.",
            ElezenData.Achievements.GetAll().Values
                .OrderBy(item => item.SortOrder)
                .ThenBy(item => item.Id),
            ownedIds,
            item => item.Id,
            item => item.Name,
            item => item.CategoryName,
            item => item.Description);
    }

    private CollectionSnapshot BuildMinionSnapshot()
    {
        var loaded = _clientState.IsLoggedIn;
        var ownedIds = ToOwnedIds(loaded, ElezenData.Minions.GetUnlocked(), item => item.Id);
        return BuildSnapshot(
            CollectionKind.Minions,
            loaded,
            string.Empty,
            ElezenData.Minions.GetAll().Values
                .OrderBy(item => item.SortOrder)
                .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase),
            ownedIds,
            item => item.Id,
            item => item.Name,
            _ => string.Empty,
            item => item.Description);
    }

    private CollectionSnapshot BuildTitleSnapshot()
    {
        var loaded = _titles.IsLoaded;
        IReadOnlySet<uint> ownedIds = loaded ? _titles.GetOwnedTitleIds() : new HashSet<uint>();
        return BuildSnapshot(
            CollectionKind.Titles,
            loaded,
            "Open the title selector in the Character window to load your obtained titles.",
            ElezenData.Titles.GetAll().Values
                .OrderBy(item => item.SortOrder)
                .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase),
            ownedIds,
            item => item.Id,
            item => item.Name,
            _ => string.Empty,
            item => item.IsPrefix ? "Prefix title" : "Suffix title");
    }

    private CollectionSnapshot BuildTripleTriadCardSnapshot()
    {
        var loaded = _clientState.IsLoggedIn;
        var ownedIds = ToOwnedIds(loaded, ElezenData.TripleTriadCards.GetUnlocked(), item => item.Id);
        return BuildSnapshot(
            CollectionKind.TripleTriadCards,
            loaded,
            string.Empty,
            ElezenData.TripleTriadCards.GetAll().Values
                .OrderBy(item => item.SortOrder)
                .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase),
            ownedIds,
            item => item.Id,
            item => item.Name,
            _ => string.Empty,
            GetTripleTriadDetail);
    }

    private CollectionSnapshot BuildMountSnapshot()
    {
        var loaded = _clientState.IsLoggedIn;
        var ownedIds = ToOwnedIds(loaded, ElezenData.Mounts.GetUnlocked(), item => item.Id);
        return BuildSnapshot(
            CollectionKind.Mounts,
            loaded,
            string.Empty,
            ElezenData.Mounts.GetAll().Values
                .OrderBy(item => item.SortOrder)
                .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase),
            ownedIds,
            item => item.Id,
            item => item.Name,
            _ => string.Empty,
            GetMountDetail);
    }

    private CollectionSnapshot BuildOrchestrionSnapshot()
    {
        var loaded = _clientState.IsLoggedIn;
        var ownedIds = ToOwnedIds(loaded, ElezenData.OrchestrionRolls.GetUnlocked(), item => item.Id);
        return BuildSnapshot(
            CollectionKind.OrchestrionRolls,
            loaded,
            string.Empty,
            ElezenData.OrchestrionRolls.GetAll().Values
                .OrderBy(item => item.SortOrder)
                .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase),
            ownedIds,
            item => item.Id,
            item => item.Name,
            _ => string.Empty,
            GetOrchestrionDetail);
    }

    private CollectionSnapshot BuildFashionAccessorySnapshot()
    {
        var loaded = _clientState.IsLoggedIn;
        var ownedIds = ToOwnedIds(loaded, ElezenData.FashionAccessories.GetUnlocked(), item => item.Id);
        return BuildSnapshot(
            CollectionKind.FashionAccessories,
            loaded,
            string.Empty,
            ElezenData.FashionAccessories.GetAll().Values
                .OrderBy(item => item.SortOrder)
                .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase),
            ownedIds,
            item => item.Id,
            item => item.Name,
            _ => string.Empty,
            item => item.Description);
    }

    private CollectionSnapshot BuildFacewearSnapshot()
    {
        var loaded = _clientState.IsLoggedIn;
        var ownedIds = ToOwnedIds(loaded, ElezenData.Facewear.GetUnlocked(), item => item.Id);
        return BuildSnapshot(
            CollectionKind.Facewear,
            loaded,
            string.Empty,
            ElezenData.Facewear.GetAll().Values
                .OrderBy(item => item.SortOrder)
                .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase),
            ownedIds,
            item => item.Id,
            item => item.Name,
            _ => string.Empty,
            GetFacewearDetail);
    }

    private CollectionSnapshot BuildSnapshot<TItem>(
        CollectionKind kind,
        bool loaded,
        string loadHint,
        IEnumerable<TItem> items,
        IReadOnlySet<uint> ownedIds,
        Func<TItem, uint> idSelector,
        Func<TItem, string> nameSelector,
        Func<TItem, string> categorySelector,
        Func<TItem, string> detailSelector)
    {
        var status = GetOwnershipStatus(loaded, loadHint);
        var ids = LimitedCollectables.GetIds(kind);
        var entries = items
            .Select(item =>
            {
                var id = idSelector(item);
                return CreateEntry(
                    kind,
                    id,
                    nameSelector(item),
                    categorySelector(item),
                    detailSelector(item),
                    loaded && ownedIds.Contains(id),
                    ids);
            })
            .ToArray();

        return new CollectionSnapshot(kind, loaded, status, entries);
    }

    private CollectableEntry CreateEntry(
        CollectionKind kind,
        uint id,
        string name,
        string category,
        string detail,
        bool owned,
        CollectableExclusionIds ids)
    {
        var limited = ids.Limited.Contains(id);
        var premium = ids.Premium.Contains(id);
        var unobtainable = ids.Unobtainable.Contains(id);
        var retiredPvp = ids.RetiredPvp.Contains(id);
        var excluded = unobtainable
            || (limited && _config.Current.ExcludeLimitedFromAttainable)
            || (premium && _config.Current.ExcludePremiumFromAttainable)
            || (retiredPvp && _config.Current.ExcludeRetiredPvpFromAttainable);

        return new CollectableEntry(kind, id, name, category, detail, owned, limited, premium, unobtainable, retiredPvp, excluded);
    }

    private AttainableSettings GetAttainableSettings()
        => new(
            _config.Current.ExcludeLimitedFromAttainable,
            _config.Current.ExcludePremiumFromAttainable,
            _config.Current.ExcludeRetiredPvpFromAttainable);

    private static HashSet<uint> ToOwnedIds<TItem>(bool loaded, IReadOnlyList<TItem> items, Func<TItem, uint> idSelector)
    {
        if (!loaded)
        {
            return [];
        }

        return items
            .Select(idSelector)
            .ToHashSet();
    }

    private static string GetTripleTriadDetail(TripleTriadCardData card)
    {
        var summary = $"Rarity {card.Rarity} | {card.Top}/{card.Right}/{card.Bottom}/{card.Left}";
        return string.IsNullOrWhiteSpace(card.Description)
            ? summary
            : $"{summary} | {card.Description}";
    }

    private static string GetMountDetail(MountData mount)
    {
        var movement = mount.IsAirborne ? "Airborne" : "Ground";
        var summary = mount.SeatCount == 1 ? movement : $"{movement} | {mount.SeatCount} seats";
        return string.IsNullOrWhiteSpace(mount.Description)
            ? summary
            : $"{summary} | {mount.Description}";
    }

    private static string GetOrchestrionDetail(OrchestrionRollData roll)
    {
        if (string.IsNullOrWhiteSpace(roll.CategoryName))
        {
            return roll.Description;
        }

        return string.IsNullOrWhiteSpace(roll.Description)
            ? roll.CategoryName
            : $"{roll.CategoryName} | {roll.Description}";
    }

    private static string GetFacewearDetail(FacewearData item)
    {
        if (string.IsNullOrWhiteSpace(item.StyleName))
        {
            return item.Description;
        }

        return string.IsNullOrWhiteSpace(item.Description)
            ? item.StyleName
            : $"{item.StyleName} | {item.Description}";
    }

    private string GetOwnershipStatus(bool loaded, string loadHint)
    {
        if (!_clientState.IsLoggedIn)
        {
            return "Log in on a character to read ownership state.";
        }

        return loaded
            ? string.Empty
            : loadHint;
    }

    private readonly record struct AttainableSettings(
        bool ExcludeLimited,
        bool ExcludePremium,
        bool ExcludeRetiredPvp);
}
