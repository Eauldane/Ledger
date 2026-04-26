using System;
using System.Globalization;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;
using Ledger.Models;
using Lumina.Excel.Sheets;

namespace Ledger.Services;

public sealed class PlayerIdentityService
{
    private readonly IFramework _framework;
    private readonly IPlayerState _playerState;
    private readonly IObjectTable _objectTable;
    private readonly IReadOnlyDictionary<ushort, string> _worldNames;
    private readonly IReadOnlyDictionary<ushort, WorldScopeInfo> _worldScopes;

    public PlayerIdentityService(IFramework framework, IPlayerState playerState, IObjectTable objectTable, IDataManager dataManager)
    {
        _framework = framework;
        _playerState = playerState;
        _objectTable = objectTable;
        (_worldNames, _worldScopes) = LoadWorldData(dataManager);
    }

    public Task<LocalPlayerIdentity?> GetLocalPlayerIdentityAsync()
        => _framework.RunOnFrameworkThread(GetLocalPlayerIdentity);

    public string ResolveWorldName(int worldId)
        => worldId > 0 && _worldNames.TryGetValue((ushort)worldId, out var worldName)
            ? worldName
            : worldId.ToString(CultureInfo.InvariantCulture);

    private LocalPlayerIdentity? GetLocalPlayerIdentity()
    {
        var characterName = _playerState.CharacterName;
        var localPlayer = _objectTable.LocalPlayer;
        if (string.IsNullOrWhiteSpace(characterName) || localPlayer is null)
        {
            return null;
        }

        var homeWorldId = (ushort)localPlayer.HomeWorld.RowId;
        if (homeWorldId == 0 || !_worldScopes.TryGetValue(homeWorldId, out var scopeInfo))
        {
            return null;
        }

        var currentWorldId = (ushort)localPlayer.CurrentWorld.RowId;
        return new LocalPlayerIdentity(
            characterName.Trim(),
            ComputeIdent(characterName.Trim(), homeWorldId),
            homeWorldId,
            currentWorldId,
            scopeInfo.DatacenterId,
            scopeInfo.RegionId);
    }

    private static (IReadOnlyDictionary<ushort, string> WorldNames, IReadOnlyDictionary<ushort, WorldScopeInfo> WorldScopes) LoadWorldData(IDataManager dataManager)
    {
        var sheet = dataManager.GetExcelSheet<World>(dataManager.Language);
        if (sheet is null)
        {
            return (new Dictionary<ushort, string>(), new Dictionary<ushort, WorldScopeInfo>());
        }

        var worldNames = new Dictionary<ushort, string>();
        var worldScopes = new Dictionary<ushort, WorldScopeInfo>();

        foreach (var row in sheet.Where(row => row.RowId > 0 && !string.IsNullOrWhiteSpace(row.Name.ToString())))
        {
            var worldId = (ushort)row.RowId;
            worldNames[worldId] = row.Name.ToString();

            var dataCenterRowId = (int)row.DataCenter.RowId;
            if (dataCenterRowId <= 0)
            {
                continue;
            }

            var dataCenter = row.DataCenter.Value;
            var datacenterId = (int)row.DataCenter.RowId;
            var regionId = ReadRegionId(dataCenter);
            if (datacenterId <= 0 || regionId <= 0)
            {
                continue;
            }

            worldScopes[worldId] = new WorldScopeInfo(datacenterId, regionId);
        }

        return (worldNames, worldScopes);
    }

    private static int ReadRegionId(object dataCenter)
    {
        var regionProperty = dataCenter.GetType().GetProperty("Region");
        if (regionProperty?.GetValue(dataCenter) is byte byteRegion)
        {
            return byteRegion;
        }

        if (regionProperty?.GetValue(dataCenter) is ushort ushortRegion)
        {
            return ushortRegion;
        }

        if (regionProperty?.GetValue(dataCenter) is int intRegion)
        {
            return intRegion;
        }

        return 0;
    }

    private static string ComputeIdent(string name, ushort worldId)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(name + worldId.ToString(CultureInfo.InvariantCulture)));
        return Convert.ToHexString(bytes);
    }

    private sealed record WorldScopeInfo(int DatacenterId, int RegionId);
}
