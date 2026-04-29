using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;
using ElezenTools.Data;
using ElezenTools.Data.Classes;
using Ledger.Models;

namespace Ledger.Services;

public sealed class PlayerIdentityService
{
    private readonly IFramework _framework;
    private readonly IPlayerState _playerState;
    private readonly IObjectTable _objectTable;
    private readonly IReadOnlyDictionary<uint, WorldData> _worldsById;

    public PlayerIdentityService(IFramework framework, IPlayerState playerState, IObjectTable objectTable)
    {
        _framework = framework;
        _playerState = playerState;
        _objectTable = objectTable;
        _worldsById = ElezenData.Worlds.GetAll();
    }

    public Task<LocalPlayerIdentity?> GetLocalPlayerIdentityAsync()
        => _framework.RunOnFrameworkThread(GetLocalPlayerIdentity);

    public LocalPlayerIdentity? GetLocalPlayerIdentityOnFrameworkThread()
        => _framework.IsInFrameworkUpdateThread
            ? GetLocalPlayerIdentity()
            : null;

    public string ResolveWorldName(int worldId)
        => worldId > 0 && _worldsById.TryGetValue((uint)worldId, out var world)
            ? world.Name
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
        if (homeWorldId == 0
            || !_worldsById.TryGetValue(homeWorldId, out var homeWorld)
            || homeWorld.DataCenterId == 0
            || homeWorld.RegionId == 0)
        {
            return null;
        }

        var currentWorldId = (ushort)localPlayer.CurrentWorld.RowId;
        return new LocalPlayerIdentity(
            characterName.Trim(),
            ComputeIdent(characterName.Trim(), homeWorldId),
            homeWorldId,
            currentWorldId,
            (int)homeWorld.DataCenterId,
            (int)homeWorld.RegionId);
    }

    public static string ComputeIdent(string name, uint worldId)
    {
        var normalisedName = name.Trim();
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(normalisedName + worldId.ToString(CultureInfo.InvariantCulture)));
        return Convert.ToHexString(bytes);
    }

}
