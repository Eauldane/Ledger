using System;
using System.Collections.Generic;
using System.Linq;
using Ledger.Data;
using Ledger.Models;
using Dalamud.Game;
using Dalamud.Utility;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;
using Lumina.Text.ReadOnly;

namespace Ledger.Services;

public sealed class CollectionDataService
{
    private static readonly TimeSpan CacheDuration = TimeSpan.FromSeconds(2);

    private readonly IUnlockState _unlockState;
    private readonly IClientState _clientState;
    private readonly ConfigService _config;
    private readonly ClientLanguage _language;
    private readonly IReadOnlyList<Achievement> _achievements;
    private readonly IReadOnlyDictionary<uint, string> _achievementCategories;
    private readonly IReadOnlyList<Companion> _companions;
    private readonly IReadOnlyDictionary<uint, CompanionTransient> _companionDetails;
    private readonly IReadOnlyList<Title> _titles;
    private readonly IReadOnlyList<TripleTriadCard> _tripleTriadCards;
    private readonly IReadOnlyDictionary<uint, TripleTriadCardResident> _tripleTriadCardDetails;
    private readonly IReadOnlyList<Mount> _mounts;
    private readonly IReadOnlyDictionary<uint, MountTransient> _mountDetails;
    private readonly IReadOnlyList<Orchestrion> _orchestrionRolls;
    private readonly IReadOnlyDictionary<uint, OrchestrionUiparam> _orchestrionDetails;
    private readonly IReadOnlyDictionary<uint, string> _orchestrionCategories;
    private readonly IReadOnlyList<Ornament> _fashionAccessories;
    private readonly IReadOnlyDictionary<uint, OrnamentTransient> _fashionAccessoryDetails;
    private readonly IReadOnlyList<Glasses> _facewear;
    private readonly IReadOnlyDictionary<uint, GlassesStyle> _facewearStyles;
    private readonly Dictionary<CollectionKind, CollectionSnapshot> _snapshotCache = new();
    private AttainableSettings _cachedSettings;
    private DateTime _cacheExpiresUtc;

    public CollectionDataService(IDataManager dataManager, IUnlockState unlockState, IClientState clientState, ConfigService config)
    {
        _unlockState = unlockState;
        _clientState = clientState;
        _config = config;
        _language = dataManager.Language;
        _achievementCategories = LoadAchievementCategories(dataManager);
        _achievements = LoadAchievements(dataManager);
        _companions = LoadCompanions(dataManager);
        _companionDetails = LoadCompanionDetails(dataManager);
        _titles = LoadTitles(dataManager);
        _tripleTriadCardDetails = LoadTripleTriadCardDetails(dataManager);
        _tripleTriadCards = LoadTripleTriadCards(dataManager);
        _mountDetails = LoadMountDetails(dataManager);
        _mounts = LoadMounts(dataManager);
        _orchestrionCategories = LoadOrchestrionCategories(dataManager);
        _orchestrionDetails = LoadOrchestrionDetails(dataManager);
        _orchestrionRolls = LoadOrchestrionRolls(dataManager);
        _fashionAccessoryDetails = LoadFashionAccessoryDetails(dataManager);
        _fashionAccessories = LoadFashionAccessories(dataManager);
        _facewearStyles = LoadFacewearStyles(dataManager);
        _facewear = LoadFacewear(dataManager);
    }

    public void Invalidate()
    {
        _snapshotCache.Clear();
        _cacheExpiresUtc = DateTime.MinValue;
    }

    public CollectionSnapshot GetSnapshot(CollectionKind kind)
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

    private CollectionSnapshot BuildAchievementSnapshot()
    {
        var loaded = _clientState.IsLoggedIn && _unlockState.IsAchievementListLoaded;
        var status = GetOwnershipStatus(loaded, "Open the achievements window to load your achievements.");
        var ids = LimitedCollectables.GetIds(CollectionKind.Achievements);
        var entries = _achievements
            .Select(row =>
            {
                var category = _achievementCategories.TryGetValue(row.AchievementCategory.RowId, out var value) ? value : string.Empty;
                var detail = category.Length > 0 ? $"{category} | {row.Description}" : row.Description.ToString();
                return CreateEntry(
                    CollectionKind.Achievements,
                    row.RowId,
                    row.Name.ToString(),
                    detail,
                    loaded && _unlockState.IsAchievementComplete(row),
                    ids);
            })
            .ToArray();

        return new CollectionSnapshot(CollectionKind.Achievements, loaded, status, entries);
    }

    private CollectionSnapshot BuildMinionSnapshot()
    {
        var loaded = _clientState.IsLoggedIn;
        var status = GetOwnershipStatus(loaded, string.Empty);
        var ids = LimitedCollectables.GetIds(CollectionKind.Minions);
        var entries = _companions
            .Select(row =>
            {
                var detail = _companionDetails.TryGetValue(row.RowId, out var transient)
                    ? FirstNonEmpty(transient.Tooltip.ToString(), transient.Description.ToString(), transient.DescriptionEnhanced.ToString())
                    : string.Empty;

                return CreateEntry(
                    CollectionKind.Minions,
                    row.RowId,
                    GetDisplayName(row.Singular),
                    detail,
                    loaded && _unlockState.IsCompanionUnlocked(row),
                    ids);
            })
            .ToArray();

        return new CollectionSnapshot(CollectionKind.Minions, loaded, status, entries);
    }

    private CollectionSnapshot BuildTitleSnapshot()
    {
        var loaded = _clientState.IsLoggedIn && _unlockState.IsTitleListLoaded;
        var status = GetOwnershipStatus(loaded, "Open the title selector in the Character window to load your obtained titles.");
        var ids = LimitedCollectables.GetIds(CollectionKind.Titles);
        var entries = _titles
            .Select(row =>
            {
                var name = GetTitleName(row);
                var detail = row.IsPrefix ? "Prefix title" : "Suffix title";
                return CreateEntry(
                    CollectionKind.Titles,
                    row.RowId,
                    name,
                    detail,
                    loaded && _unlockState.IsTitleUnlocked(row),
                    ids);
            })
            .ToArray();

        return new CollectionSnapshot(CollectionKind.Titles, loaded, status, entries);
    }

    private CollectionSnapshot BuildTripleTriadCardSnapshot()
    {
        var loaded = _clientState.IsLoggedIn;
        var status = GetOwnershipStatus(loaded, string.Empty);
        var ids = LimitedCollectables.GetIds(CollectionKind.TripleTriadCards);
        var entries = _tripleTriadCards
            .Select(row =>
            {
                var detail = GetTripleTriadDetail(row);
                return CreateEntry(
                    CollectionKind.TripleTriadCards,
                    row.RowId,
                    row.Name.ToString(),
                    detail,
                    loaded && _unlockState.IsTripleTriadCardUnlocked(row),
                    ids);
            })
            .ToArray();

        return new CollectionSnapshot(CollectionKind.TripleTriadCards, loaded, status, entries);
    }

    private CollectionSnapshot BuildMountSnapshot()
    {
        var loaded = _clientState.IsLoggedIn;
        var status = GetOwnershipStatus(loaded, string.Empty);
        var ids = LimitedCollectables.GetIds(CollectionKind.Mounts);
        var entries = _mounts
            .Select(row =>
            {
                var detail = GetMountDetail(row);
                return CreateEntry(
                    CollectionKind.Mounts,
                    row.RowId,
                    GetDisplayName(row.Singular),
                    detail,
                    loaded && _unlockState.IsMountUnlocked(row),
                    ids);
            })
            .ToArray();

        return new CollectionSnapshot(CollectionKind.Mounts, loaded, status, entries);
    }

    private CollectionSnapshot BuildOrchestrionSnapshot()
    {
        var loaded = _clientState.IsLoggedIn;
        var status = GetOwnershipStatus(loaded, string.Empty);
        var ids = LimitedCollectables.GetIds(CollectionKind.OrchestrionRolls);
        var entries = _orchestrionRolls
            .Select(row =>
            {
                var detail = GetOrchestrionDetail(row);
                return CreateEntry(
                    CollectionKind.OrchestrionRolls,
                    row.RowId,
                    row.Name.ToString(),
                    detail,
                    loaded && _unlockState.IsOrchestrionUnlocked(row),
                    ids);
            })
            .ToArray();

        return new CollectionSnapshot(CollectionKind.OrchestrionRolls, loaded, status, entries);
    }

    private CollectionSnapshot BuildFashionAccessorySnapshot()
    {
        var loaded = _clientState.IsLoggedIn;
        var status = GetOwnershipStatus(loaded, string.Empty);
        var ids = LimitedCollectables.GetIds(CollectionKind.FashionAccessories);
        var entries = _fashionAccessories
            .Select(row =>
            {
                var detail = GetFashionAccessoryDetail(row);
                return CreateEntry(
                    CollectionKind.FashionAccessories,
                    row.RowId,
                    row.Singular.ToString(),
                    detail,
                    loaded && _unlockState.IsOrnamentUnlocked(row),
                    ids);
            })
            .ToArray();

        return new CollectionSnapshot(CollectionKind.FashionAccessories, loaded, status, entries);
    }

    private CollectionSnapshot BuildFacewearSnapshot()
    {
        var loaded = _clientState.IsLoggedIn;
        var status = GetOwnershipStatus(loaded, string.Empty);
        var ids = LimitedCollectables.GetIds(CollectionKind.Facewear);
        var entries = _facewear
            .Select(row =>
            {
                var detail = GetFacewearDetail(row);
                return CreateEntry(
                    CollectionKind.Facewear,
                    row.RowId,
                    GetFacewearName(row),
                    detail,
                    loaded && _unlockState.IsGlassesUnlocked(row),
                    ids);
            })
            .ToArray();

        return new CollectionSnapshot(CollectionKind.Facewear, loaded, status, entries);
    }

    private CollectableEntry CreateEntry(
        CollectionKind kind,
        uint id,
        string name,
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

        return new CollectableEntry(kind, id, name, detail, owned, limited, premium, unobtainable, retiredPvp, excluded);
    }

    private AttainableSettings GetAttainableSettings()
        => new(
            _config.Current.ExcludeLimitedFromAttainable,
            _config.Current.ExcludePremiumFromAttainable,
            _config.Current.ExcludeRetiredPvpFromAttainable);

    private string GetTripleTriadDetail(TripleTriadCard row)
    {
        if (!_tripleTriadCardDetails.TryGetValue(row.RowId, out var resident))
        {
            return row.Description.ToString();
        }

        var rarity = resident.TripleTriadCardRarity.RowId;
        var stats = $"{resident.Top}/{resident.Right}/{resident.Bottom}/{resident.Left}";
        var description = row.Description.ToString();
        return string.IsNullOrWhiteSpace(description)
            ? $"Rarity {rarity} | {stats}"
            : $"Rarity {rarity} | {stats} | {description}";
    }

    private string GetMountDetail(Mount row)
    {
        var seats = Math.Max(1, row.ExtraSeats + 1);
        var movement = row.IsAirborne ? "Airborne" : "Ground";
        var summary = seats == 1 ? movement : $"{movement} | {seats} seats";

        if (!_mountDetails.TryGetValue(row.RowId, out var transient))
        {
            return summary;
        }

        var description = FirstNonEmpty(
            transient.Tooltip.ToString(),
            transient.Description.ToString(),
            transient.DescriptionEnhanced.ToString());

        return string.IsNullOrWhiteSpace(description)
            ? summary
            : $"{summary} | {description}";
    }

    private string GetOrchestrionDetail(Orchestrion row)
    {
        var description = row.Description.ToString();
        if (!_orchestrionDetails.TryGetValue(row.RowId, out var uiParam)
            || !_orchestrionCategories.TryGetValue(uiParam.OrchestrionCategory.RowId, out var category))
        {
            return description;
        }

        return string.IsNullOrWhiteSpace(description)
            ? category
            : $"{category} | {description}";
    }

    private string GetFashionAccessoryDetail(Ornament row)
    {
        if (_fashionAccessoryDetails.TryGetValue(row.Transient, out var transient))
        {
            return transient.Text.ToString();
        }

        return string.Empty;
    }

    private string GetFacewearDetail(Glasses row)
    {
        var description = row.Description.ToString();
        if (!_facewearStyles.TryGetValue(row.Style.RowId, out var style))
        {
            return description;
        }

        var styleName = FirstNonEmpty(style.Name.ToString(), style.Singular.ToString());
        return string.IsNullOrWhiteSpace(description)
            ? styleName
            : $"{styleName} | {description}";
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

    private string GetDisplayName(in ReadOnlySeString name)
        => string.Intern(name.ExtractText().ToUpper(true, true, false, _language));

    private static IReadOnlyList<Achievement> LoadAchievements(IDataManager dataManager)
    {
        var sheet = dataManager.GetExcelSheet<Achievement>(dataManager.Language);
        if (sheet is null)
        {
            return [];
        }

        return sheet
            .Where(row => row.RowId > 0 && !string.IsNullOrWhiteSpace(row.Name.ToString()))
            .OrderBy(row => row.Order)
            .ThenBy(row => row.RowId)
            .ToArray();
    }

    private static IReadOnlyDictionary<uint, string> LoadAchievementCategories(IDataManager dataManager)
    {
        var sheet = dataManager.GetExcelSheet<AchievementCategory>(dataManager.Language);
        if (sheet is null)
        {
            return new Dictionary<uint, string>();
        }

        return sheet
            .Where(row => row.RowId > 0 && !string.IsNullOrWhiteSpace(row.Name.ToString()))
            .ToDictionary(row => row.RowId, row => row.Name.ToString());
    }

    private static IReadOnlyList<Companion> LoadCompanions(IDataManager dataManager)
    {
        var sheet = dataManager.GetExcelSheet<Companion>(dataManager.Language);
        if (sheet is null)
        {
            return [];
        }

        return sheet
            .Where(row => row.RowId > 0 && !string.IsNullOrWhiteSpace(row.Singular.ToString()))
            .OrderBy(row => row.Order)
            .ThenBy(row => row.Singular.ToString(), StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static IReadOnlyDictionary<uint, CompanionTransient> LoadCompanionDetails(IDataManager dataManager)
    {
        var sheet = dataManager.GetExcelSheet<CompanionTransient>(dataManager.Language);
        if (sheet is null)
        {
            return new Dictionary<uint, CompanionTransient>();
        }

        return sheet
            .Where(row => row.RowId > 0)
            .ToDictionary(row => row.RowId, row => row);
    }

    private static IReadOnlyList<Title> LoadTitles(IDataManager dataManager)
    {
        var sheet = dataManager.GetExcelSheet<Title>(dataManager.Language);
        if (sheet is null)
        {
            return [];
        }

        return sheet
            .Where(row => row.RowId > 0 && !string.IsNullOrWhiteSpace(GetTitleName(row)))
            .OrderBy(row => row.Order)
            .ThenBy(GetTitleName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static IReadOnlyDictionary<uint, TripleTriadCardResident> LoadTripleTriadCardDetails(IDataManager dataManager)
    {
        var sheet = dataManager.GetExcelSheet<TripleTriadCardResident>(dataManager.Language);
        if (sheet is null)
        {
            return new Dictionary<uint, TripleTriadCardResident>();
        }

        return sheet
            .Where(row => row.RowId > 0)
            .ToDictionary(row => row.RowId, row => row);
    }

    private IReadOnlyList<TripleTriadCard> LoadTripleTriadCards(IDataManager dataManager)
    {
        var sheet = dataManager.GetExcelSheet<TripleTriadCard>(dataManager.Language);
        if (sheet is null)
        {
            return [];
        }

        return sheet
            .Where(row => row.RowId > 0 && !string.IsNullOrWhiteSpace(row.Name.ToString()))
            .OrderBy(row => _tripleTriadCardDetails.TryGetValue(row.RowId, out var resident) ? resident.Order : ushort.MaxValue)
            .ThenBy(row => row.Name.ToString(), StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static IReadOnlyDictionary<uint, MountTransient> LoadMountDetails(IDataManager dataManager)
    {
        var sheet = dataManager.GetExcelSheet<MountTransient>(dataManager.Language);
        if (sheet is null)
        {
            return new Dictionary<uint, MountTransient>();
        }

        return sheet
            .Where(row => row.RowId > 0)
            .ToDictionary(row => row.RowId, row => row);
    }

    private static IReadOnlyList<Mount> LoadMounts(IDataManager dataManager)
    {
        var sheet = dataManager.GetExcelSheet<Mount>(dataManager.Language);
        if (sheet is null)
        {
            return [];
        }

        return sheet
            .Where(row => row.RowId > 0 && !string.IsNullOrWhiteSpace(row.Singular.ToString()))
            .OrderBy(row => row.Order < 0 ? short.MaxValue : row.Order)
            .ThenBy(row => row.Singular.ToString(), StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static IReadOnlyDictionary<uint, string> LoadOrchestrionCategories(IDataManager dataManager)
    {
        var sheet = dataManager.GetExcelSheet<OrchestrionCategory>(dataManager.Language);
        if (sheet is null)
        {
            return new Dictionary<uint, string>();
        }

        return sheet
            .Where(row => row.RowId > 0 && !string.IsNullOrWhiteSpace(row.Name.ToString()))
            .ToDictionary(row => row.RowId, row => row.Name.ToString());
    }

    private static IReadOnlyDictionary<uint, OrchestrionUiparam> LoadOrchestrionDetails(IDataManager dataManager)
    {
        var sheet = dataManager.GetExcelSheet<OrchestrionUiparam>(dataManager.Language);
        if (sheet is null)
        {
            return new Dictionary<uint, OrchestrionUiparam>();
        }

        return sheet
            .Where(row => row.RowId > 0)
            .ToDictionary(row => row.RowId, row => row);
    }

    private IReadOnlyList<Orchestrion> LoadOrchestrionRolls(IDataManager dataManager)
    {
        var sheet = dataManager.GetExcelSheet<Orchestrion>(dataManager.Language);
        if (sheet is null)
        {
            return [];
        }

        return sheet
            .Where(row => row.RowId > 0 && !string.IsNullOrWhiteSpace(row.Name.ToString()))
            .OrderBy(row => _orchestrionDetails.TryGetValue(row.RowId, out var details) ? details.Order : ushort.MaxValue)
            .ThenBy(row => row.Name.ToString(), StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static IReadOnlyDictionary<uint, OrnamentTransient> LoadFashionAccessoryDetails(IDataManager dataManager)
    {
        var sheet = dataManager.GetExcelSheet<OrnamentTransient>(dataManager.Language);
        if (sheet is null)
        {
            return new Dictionary<uint, OrnamentTransient>();
        }

        return sheet
            .Where(row => row.RowId > 0)
            .ToDictionary(row => row.RowId, row => row);
    }

    private static IReadOnlyList<Ornament> LoadFashionAccessories(IDataManager dataManager)
    {
        var sheet = dataManager.GetExcelSheet<Ornament>(dataManager.Language);
        if (sheet is null)
        {
            return [];
        }

        return sheet
            .Where(row => row.RowId > 0 && !string.IsNullOrWhiteSpace(row.Singular.ToString()))
            .OrderBy(row => row.Order < 0 ? short.MaxValue : row.Order)
            .ThenBy(row => row.Singular.ToString(), StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static IReadOnlyDictionary<uint, GlassesStyle> LoadFacewearStyles(IDataManager dataManager)
    {
        var sheet = dataManager.GetExcelSheet<GlassesStyle>(dataManager.Language);
        if (sheet is null)
        {
            return new Dictionary<uint, GlassesStyle>();
        }

        return sheet
            .Where(row => row.RowId > 0)
            .ToDictionary(row => row.RowId, row => row);
    }

    private IReadOnlyList<Glasses> LoadFacewear(IDataManager dataManager)
    {
        var sheet = dataManager.GetExcelSheet<Glasses>(dataManager.Language);
        if (sheet is null)
        {
            return [];
        }

        return sheet
            .Where(row => row.RowId > 0 && !string.IsNullOrWhiteSpace(GetFacewearName(row)))
            .OrderBy(row => _facewearStyles.TryGetValue(row.Style.RowId, out var style) ? style.Order : ushort.MaxValue)
            .ThenBy(GetFacewearName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string GetTitleName(Title row)
    {
        var masculine = row.Masculine.ToString();
        var feminine = row.Feminine.ToString();

        if (string.IsNullOrWhiteSpace(masculine))
        {
            return feminine;
        }

        if (string.IsNullOrWhiteSpace(feminine) || string.Equals(masculine, feminine, StringComparison.Ordinal))
        {
            return masculine;
        }

        return $"{masculine} / {feminine}";
    }

    private static string GetFacewearName(Glasses row)
        => FirstNonEmpty(row.Name.ToString(), row.Singular.ToString());

    private static string FirstNonEmpty(params string[] values)
        => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;

    private readonly record struct AttainableSettings(
        bool ExcludeLimited,
        bool ExcludePremium,
        bool ExcludeRetiredPvp);
}
