using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Cairo;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.API.Util;
using Vintagestory.GameContent;

using Lang = Vintagestory.API.Config.Lang;

namespace PlayerPositionTracker;

public class PlayerPositionMapLayer : MapLayer
{
    private static readonly double[][] PlayerColors =
    {
        new[] { 1.0, 0.2, 0.2, 1.0 },
        new[] { 0.2, 0.6, 1.0, 1.0 },
        new[] { 0.2, 1.0, 0.3, 1.0 },
        new[] { 1.0, 0.8, 0.1, 1.0 },
        new[] { 0.8, 0.3, 1.0, 1.0 },
        new[] { 1.0, 0.5, 0.0, 1.0 },
        new[] { 0.0, 1.0, 0.8, 1.0 },
        new[] { 1.0, 0.4, 0.7, 1.0 },
    };

    private ICoreClientAPI _capi;
    private PlayerPositionTrackerModSystem _modSystem;
    private readonly Dictionary<string, LoadedTexture> _playerTextures = new();

    private List<string> _availableDates = new();
    private List<PlayerPositionRecord> _currentRecords = new();
    private List<PositionMapComponent> _components = new();
    private Dictionary<string, string> _playerNames = new();
    private string _selectedDate;
    private int _sliderValue;
    private string _selectedPlayerFilter = "__all__";
    private HashSet<string> _filteredPlayerUids = new();

    public override string Title => "Player Position History";
    public override EnumMapAppSide DataSide => EnumMapAppSide.Server;
    public override string LayerGroupCode => "playerpositiontracker";
    public override bool RequireChunkLoaded => false;

    public PlayerPositionMapLayer(ICoreAPI api, IWorldMapManager mapSink) : base(api, mapSink)
    {
        _modSystem = api.ModLoader.GetModSystem<PlayerPositionTrackerModSystem>();

        if (api.Side == EnumAppSide.Client)
        {
            _capi = api as ICoreClientAPI;
            _modSystem.OnResponseReceived += OnPositionDataReceived;
        }

        Active = false;
    }

    public override void OnMapOpenedClient()
    {
        if (_currentRecords.Count > 0)
        {
            RebuildComponents();
        }
    }

    public override void OnMapClosedClient()
    {
        DisposeComponents();
    }

    public override void ComposeDialogExtras(GuiDialogWorldMap guiDialogWorldMap, GuiComposer compo)
    {
        var key = "worldmap-layer-" + LayerGroupCode;

        var bounds = ElementStdBounds.AutosizedMainDialog
            .WithFixedPosition(
                (compo.Bounds.renderX + compo.Bounds.OuterWidth) / (double)RuntimeEnv.GUIScale + 10.0,
                compo.Bounds.renderY / (double)RuntimeEnv.GUIScale)
            .WithAlignment(EnumDialogArea.None);

        var bgBounds = ElementBounds.Fill.WithFixedPadding(GuiStyle.ElementToDialogPadding);
        bgBounds.BothSizing = ElementSizing.FitToChildren;

        if (!IsClientAuthorized())
        {
            guiDialogWorldMap.Composers[key] = _capi.Gui.CreateCompo(key, bounds)
                .AddShadedDialogBG(bgBounds, withTitleBar: false)
                .AddDialogTitleBar(Lang.Get("playerpositiontracker:dialog-title"), () => { guiDialogWorldMap.Composers[key].Enabled = false; })
                .BeginChildElements(bgBounds)
                .AddStaticText(Lang.Get("playerpositiontracker:unauthorized"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(0, 30, 300, 25))
                .EndChildElements()
                .Compose();
            guiDialogWorldMap.Composers[key].Enabled = false;
            return;
        }

        var noData = Lang.Get("playerpositiontracker:no-data");
        var dates = _availableDates.Count > 0 ? _availableDates.ToArray() : new[] { noData };
        var selectedIndex = _selectedDate != null ? Math.Max(0, dates.IndexOf(_selectedDate)) : 0;

        var playerUids = _currentRecords.Select(r => r.PlayerUid).Distinct().OrderBy(uid =>
            _playerNames.TryGetValue(uid, out var n) ? n : uid).ToArray();
        var playerLabels = playerUids.Select(uid =>
            _playerNames.TryGetValue(uid, out var n) ? n : uid).ToArray();

        var tickCount = GetDistinctTimestampCount();
        var maxSlider = Math.Max(1, tickCount - 1);

        var composer = _capi.Gui.CreateCompo(key, bounds)
            .AddShadedDialogBG(bgBounds, withTitleBar: false)
            .AddDialogTitleBar(Lang.Get("playerpositiontracker:dialog-title"), () => { guiDialogWorldMap.Composers[key].Enabled = false; })
            .BeginChildElements(bgBounds)
            .AddStaticText(Lang.Get("playerpositiontracker:label-date"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(0, 30, 60, 25))
            .AddDropDown(
                dates, dates, selectedIndex,
                OnDateSelected,
                ElementBounds.Fixed(60, 27, 140, 25),
                "dateDropdown")
            .AddStaticText(Lang.Get("playerpositiontracker:label-time"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(0, 70, 60, 25))
            .AddSlider(OnSliderChanged, ElementBounds.Fixed(60, 65, 250, 30), "timeSlider")
            .AddDynamicText("", CairoFont.WhiteSmallText(), ElementBounds.Fixed(60, 105, 310, 25), "timeDisplay")
            .AddStaticText(Lang.Get("playerpositiontracker:label-player"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(0, 140, 60, 25));

        if (playerUids.Length > 0)
        {
            var playerValues = new[] { "__all__" }.Concat(playerUids).ToArray();
            var playerFilterIndex = Math.Max(0, Array.IndexOf(playerValues, _selectedPlayerFilter));
            composer.AddDropDown(
                playerValues,
                new[] { Lang.Get("playerpositiontracker:all-players") }.Concat(playerLabels).ToArray(),
                playerFilterIndex,
                OnPlayerFilterChanged,
                ElementBounds.Fixed(60, 138, 140, 25),
                "playerFilter");
        }

        composer.AddSmallButton(Lang.Get("playerpositiontracker:refresh"), OnRefreshClicked,
            ElementBounds.Fixed(0, 175, 100, 25));

        guiDialogWorldMap.Composers[key] = composer.EndChildElements().Compose();

        var slider = guiDialogWorldMap.Composers[key].GetSlider("timeSlider");
        slider?.SetValues(Math.Min(_sliderValue, maxSlider), 0, maxSlider, 1);

        UpdateTimeDisplay(guiDialogWorldMap.Composers[key]);

        guiDialogWorldMap.Composers[key].Enabled = false;
    }

    public override void Render(GuiElementMap mapElem, float dt)
    {
        if (!Active || !IsClientAuthorized()) return;
        foreach (var comp in _components)
        {
            comp.Render(mapElem, dt);
        }
    }

    public override void OnMouseMoveClient(MouseEvent args, GuiElementMap mapElem, StringBuilder hoverText)
    {
        if (!Active || !IsClientAuthorized()) return;
        foreach (var comp in _components)
        {
            comp.OnMouseMove(args, mapElem, hoverText);
        }
    }

    public override void Dispose()
    {
        if (_modSystem != null)
        {
            _modSystem.OnResponseReceived -= OnPositionDataReceived;
        }

        DisposeComponents();
        DisposePlayerTextures();
    }

    private void OnPositionDataReceived(PositionDataResponse response)
    {
        if (response == null) return;

        if (response.AvailableDates != null)
        {
            _availableDates = response.AvailableDates;
        }

        if (response.PlayerNames != null)
        {
            _playerNames = response.PlayerNames;
        }

        if (response.Records != null && response.Records.Count > 0)
        {
            _currentRecords = response.Records;
            var allUids = _currentRecords.Select(r => r.PlayerUid).Distinct();
            _filteredPlayerUids = _selectedPlayerFilter == "__all__"
                ? new HashSet<string>(allUids)
                : new HashSet<string> { _selectedPlayerFilter };
        }

        if (_selectedDate == null && _availableDates.Count > 0 && _currentRecords.Count == 0)
        {
            _selectedDate = _availableDates[0];
            _modSystem.RequestDateData(_selectedDate, _selectedPlayerFilter);
            return;
        }

        RebuildComponents();
        RecomposeDialog();
    }

    private void OnDateSelected(string date, bool selected)
    {
        if (date == Lang.Get("playerpositiontracker:no-data")) return;
        _selectedDate = date;
        _sliderValue = 0;
        _modSystem.RequestDateData(date, _selectedPlayerFilter);
    }

    private bool OnSliderChanged(int value)
    {
        _sliderValue = value;
        RebuildComponents();
        UpdateTimeDisplayInDialog();
        return true;
    }

    private bool OnRefreshClicked()
    {
        if (!string.IsNullOrEmpty(_selectedDate))
        {
            _modSystem.RequestDateData(_selectedDate, _selectedPlayerFilter);
        }
        return true;
    }

    private void OnPlayerFilterChanged(string uid, bool selected)
    {
        _selectedPlayerFilter = uid;
        var allUids = _currentRecords.Select(r => r.PlayerUid).Distinct();
        _filteredPlayerUids = uid == "__all__"
            ? new HashSet<string>(allUids)
            : new HashSet<string> { uid };
        RebuildComponents();
        if (!string.IsNullOrEmpty(_selectedDate))
        {
            _modSystem.RequestDateData(_selectedDate, _selectedPlayerFilter);
        }
        if (uid != "__all__")
        {
            CenterMapOnPlayer(uid);
        }
    }

    private void CenterMapOnPlayer(string playerUid)
    {
        var timestamps = _currentRecords.Select(r => r.Timestamp).Distinct().OrderBy(t => t).ToList();
        if (_sliderValue >= timestamps.Count) return;

        var targetTimestamp = timestamps[_sliderValue];
        var record = _currentRecords.FirstOrDefault(r => r.Timestamp == targetTimestamp && r.PlayerUid == playerUid);
        if (record == null) return;

        var mapManager = _capi.ModLoader.GetModSystem<WorldMapManager>();
        var mapElem = mapManager.worldMapDlg?.SingleComposer?.GetElement("mapElem") as GuiElementMap;
        mapElem?.CenterMapTo(new BlockPos((int)record.X, (int)record.Y, (int)record.Z));
    }

    private void UpdateTimeDisplayInDialog()
    {
        if (_capi == null) return;
        var mapManager = _capi.ModLoader.GetModSystem<WorldMapManager>();
        if (mapManager.worldMapDlg == null) return;
        var key = "worldmap-layer-" + LayerGroupCode;
        if (!mapManager.worldMapDlg.Composers.ContainsKey(key)) return;
        UpdateTimeDisplay(mapManager.worldMapDlg.Composers[key]);
    }

    private void UpdateTimeDisplay(GuiComposer composer)
    {
        var textElem = composer.GetDynamicText("timeDisplay");
        if (textElem == null) return;
        var timestamps = _currentRecords.Select(r => r.Timestamp).Distinct().OrderBy(t => t).ToList();
        if (_sliderValue < timestamps.Count && DateTime.TryParse(timestamps[_sliderValue], out var utcTime))
        {
            var local = utcTime.ToLocalTime();
            var tz = TimeZoneInfo.Local;
            var abbr = tz.IsDaylightSavingTime(local) ? tz.DaylightName : tz.StandardName;
            textElem.SetNewText($"{local:HH:mm:ss} {abbr}");
        }
        else
        {
            textElem.SetNewText("");
        }
    }

    private void EnsurePlayerTextures()
    {
        var uids = _currentRecords.Select(r => r.PlayerUid).Distinct().ToList();
        foreach (var uid in uids)
        {
            if (_playerTextures.ContainsKey(uid)) continue;
            var colorIndex = _playerTextures.Count % PlayerColors.Length;
            var color = PlayerColors[colorIndex];
            int size = (int)GuiElement.scaled(32.0);
            var surface = new ImageSurface(Format.Argb32, size, size);
            var ctx = new Context(surface);
            ctx.SetSourceRGBA(0.0, 0.0, 0.0, 0.0);
            ctx.Paint();
            _capi.Gui.Icons.DrawMapPlayer(ctx, 0, 0, size, size,
                new[] { 0.0, 0.0, 0.0, 1.0 }, color);
            _playerTextures[uid] = new LoadedTexture(_capi, _capi.Gui.LoadCairoTexture(surface, false), size / 2, size / 2);
            ctx.Dispose();
            ((Surface)surface).Dispose();
        }
    }

    private void DisposePlayerTextures()
    {
        foreach (var tex in _playerTextures.Values) tex.Dispose();
        _playerTextures.Clear();
    }

    private void RebuildComponents()
    {
        DisposeComponents();
        if (_currentRecords.Count == 0) return;

        EnsurePlayerTextures();

        var timestamps = _currentRecords.Select(r => r.Timestamp).Distinct().OrderBy(t => t).ToList();
        if (_sliderValue >= timestamps.Count) return;

        var targetTimestamp = timestamps[_sliderValue];
        var filtered = _currentRecords
            .Where(r => r.Timestamp == targetTimestamp && _filteredPlayerUids.Contains(r.PlayerUid))
            .ToList();

        foreach (var rec in filtered)
        {
            if (!_playerTextures.TryGetValue(rec.PlayerUid, out var tex)) continue;
            var pos = new Vec3d(rec.X, rec.Y, rec.Z);
            var name = _playerNames.TryGetValue(rec.PlayerUid, out var n) ? n : rec.PlayerUid;
            _components.Add(new PositionMapComponent(_capi, tex, pos, name, rec.Timestamp, rec.Yaw));
        }
    }

    private void DisposeComponents()
    {
        foreach (var comp in _components)
        {
            comp.Dispose();
        }

        _components.Clear();
    }

    private bool IsClientAuthorized()
    {
        var player = _capi?.World?.Player;
        return player?.Role?.Code == "admin" &&
               player.WorldData?.CurrentGameMode == EnumGameMode.Creative;
    }

    private int GetDistinctTimestampCount()
    {
        return _currentRecords.Select(r => r.Timestamp).Distinct().Count();
    }

    private void RecomposeDialog()
    {
        if (_capi == null) return;
        var mapManager = _capi.ModLoader.GetModSystem<WorldMapManager>();
        if (mapManager.worldMapDlg == null) return;

        var key = "worldmap-layer-" + LayerGroupCode;
        var wasEnabled = mapManager.worldMapDlg.Composers.ContainsKey(key) &&
                         mapManager.worldMapDlg.Composers[key].Enabled;

        ComposeDialogExtras(mapManager.worldMapDlg, mapManager.worldMapDlg.SingleComposer);

        if (wasEnabled && mapManager.worldMapDlg.Composers.ContainsKey(key))
        {
            mapManager.worldMapDlg.Composers[key].Enabled = true;
        }
    }
}
