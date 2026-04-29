using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Plugin.Services;
using Title = Lumina.Excel.Sheets.Title;

namespace Ledger.Services;

public sealed class TitleTrackingService : IDisposable
{
    private static readonly string[] TitleAddonNames = ["CharacterTitle", "CharacterTitleSelect"];

    private readonly IAddonLifecycle _addonLifecycle;
    private readonly IClientState _clientState;
    private readonly IDataManager _dataManager;
    private readonly IUnlockState _unlockState;
    private readonly object _sync = new();

    private HashSet<uint> _ownedTitleIds = [];
    private bool _loaded;

    public TitleTrackingService(
        IAddonLifecycle addonLifecycle,
        IClientState clientState,
        IDataManager dataManager,
        IUnlockState unlockState)
    {
        _addonLifecycle = addonLifecycle;
        _clientState = clientState;
        _dataManager = dataManager;
        _unlockState = unlockState;

        _clientState.Login += OnLogin;
        _clientState.Logout += OnLogout;
        _addonLifecycle.RegisterListener(AddonEvent.PostRequestedUpdate, TitleAddonNames, OnTitleListUpdated);

        if (_clientState.IsLoggedIn && _unlockState.IsTitleListLoaded)
        {
            Refresh();
        }
    }

    public event System.Action? Changed;

    public bool IsLoaded
    {
        get
        {
            lock (_sync)
            {
                return _loaded;
            }
        }
    }

    public IReadOnlySet<uint> GetOwnedTitleIds()
    {
        lock (_sync)
        {
            return _ownedTitleIds.Count == 0 ? [] : new HashSet<uint>(_ownedTitleIds);
        }
    }

    public void Dispose()
    {
        _addonLifecycle.UnregisterListener(AddonEvent.PostRequestedUpdate, TitleAddonNames, OnTitleListUpdated);
        _clientState.Login -= OnLogin;
        _clientState.Logout -= OnLogout;
    }

    private void OnLogin()
        => Clear();

    private void OnLogout(int type, int code)
        => Clear();

    private void OnTitleListUpdated(AddonEvent type, AddonArgs args)
        => Refresh();

    private void Refresh()
    {
        if (!_clientState.IsLoggedIn)
        {
            return;
        }

        var titleSheet = _dataManager.GetExcelSheet<Title>();
        if (titleSheet == null)
        {
            return;
        }

        var ownedTitleIds = titleSheet
            .Where(_unlockState.IsTitleUnlocked)
            .Select(row => row.RowId)
            .ToHashSet();

        var changed = false;
        lock (_sync)
        {
            if (!_loaded || !_ownedTitleIds.SetEquals(ownedTitleIds))
            {
                _ownedTitleIds = ownedTitleIds;
                _loaded = true;
                changed = true;
            }
        }

        if (changed)
        {
            Changed?.Invoke();
        }
    }

    private void Clear()
    {
        var changed = false;
        lock (_sync)
        {
            if (_loaded || _ownedTitleIds.Count > 0)
            {
                _ownedTitleIds = [];
                _loaded = false;
                changed = true;
            }
        }

        if (changed)
        {
            Changed?.Invoke();
        }
    }
}
