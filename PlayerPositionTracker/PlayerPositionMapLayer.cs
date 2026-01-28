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

namespace PlayerPositionTracker;

public class PlayerPositionMapLayer : MapLayer
{
    private ICoreClientAPI _capi;
    private PlayerPositionTrackerModSystem _modSystem;
    private LoadedTexture _dotTexture;

    private List<string> _availableDates = new();
    private List<PlayerPositionRecord> _currentRecords = new();
    private List<PositionMapComponent> _components = new();
    private string _selectedDate;
    private int _sliderValue;

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
    }

    public override void OnMapOpenedClient()
    {
        CreateDotTexture();
        _modSystem.RequestDateData("");
    }

    public override void OnMapClosedClient()
    {
        DisposeComponents();
    }

    public override void ComposeDialogExtras(GuiDialogWorldMap guiDialogWorldMap, GuiComposer compo)
    {
        var key = "worldmap-layer-" + LayerGroupCode;
        var dates = _availableDates.Count > 0 ? _availableDates.ToArray() : new[] { "(no data)" };
        var selectedIndex = _selectedDate != null ? Math.Max(0, dates.IndexOf(_selectedDate)) : 0;

        var bounds = ElementStdBounds.AutosizedMainDialog
            .WithFixedPosition(
                (compo.Bounds.renderX + compo.Bounds.OuterWidth) / (double)RuntimeEnv.GUIScale + 10.0,
                compo.Bounds.renderY / (double)RuntimeEnv.GUIScale)
            .WithAlignment(EnumDialogArea.None);

        var bgBounds = ElementBounds.Fill.WithFixedPadding(GuiStyle.ElementToDialogPadding);
        bgBounds.BothSizing = ElementSizing.FitToChildren;

        var tickCount = GetDistinctTimestampCount();
        var maxSlider = Math.Max(1, tickCount - 1);

        guiDialogWorldMap.Composers[key] = _capi.Gui.CreateCompo(key, bounds)
            .AddShadedDialogBG(bgBounds, withTitleBar: false)
            .AddDialogTitleBar("Position History", () => { guiDialogWorldMap.Composers[key].Enabled = false; })
            .BeginChildElements(bgBounds)
            .AddStaticText("Date:", CairoFont.WhiteSmallText(), ElementBounds.Fixed(0, 30, 60, 25))
            .AddDropDown(
                dates, dates, selectedIndex,
                OnDateSelected,
                ElementBounds.Fixed(60, 30, 140, 25),
                "dateDropdown")
            .AddStaticText("Time:", CairoFont.WhiteSmallText(), ElementBounds.Fixed(0, 70, 60, 25))
            .AddSlider(OnSliderChanged, ElementBounds.Fixed(60, 70, 250, 30), "timeSlider")
            .EndChildElements()
            .Compose();

        var slider = guiDialogWorldMap.Composers[key].GetSlider("timeSlider");
        slider?.SetValues(Math.Min(_sliderValue, maxSlider), 0, maxSlider, 1);

        guiDialogWorldMap.Composers[key].Enabled = false;
    }

    public override void Render(GuiElementMap mapElem, float dt)
    {
        if (!Active) return;
        foreach (var comp in _components)
        {
            comp.Render(mapElem, dt);
        }
    }

    public override void OnMouseMoveClient(MouseEvent args, GuiElementMap mapElem, StringBuilder hoverText)
    {
        if (!Active) return;
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
        _dotTexture?.Dispose();
    }

    private void OnPositionDataReceived(PositionDataResponse response)
    {
        if (response == null) return;

        if (response.AvailableDates != null)
        {
            _availableDates = response.AvailableDates;
        }

        if (response.Records != null && response.Records.Count > 0)
        {
            _currentRecords = response.Records;
            _capi?.Logger.Debug($"[MapLayer] Received {_currentRecords.Count} records, " +
                                $"{GetDistinctTimestampCount()} distinct timestamps");
        }

        RebuildComponents();
        RecomposeDialog();
    }

    private void OnDateSelected(string date, bool selected)
    {
        if (date == "(no data)") return;
        _selectedDate = date;
        _sliderValue = 0;
        _capi?.Logger.Debug($"[MapLayer] Date selected: {date}");
        _modSystem.RequestDateData(date);
    }

    private bool OnSliderChanged(int value)
    {
        _sliderValue = value;
        RebuildComponents();
        return true;
    }

    private void CreateDotTexture()
    {
        _dotTexture?.Dispose();
        int size = (int)GuiElement.scaled(32.0);
        ImageSurface surface = new ImageSurface(Format.Argb32, size, size);
        Context ctx = new Context(surface);
        ctx.SetSourceRGBA(0.0, 0.0, 0.0, 0.0);
        ctx.Paint();
        _capi.Gui.Icons.DrawMapPlayer(ctx, 0, 0, size, size,
            new double[] { 0.0, 0.0, 0.0, 1.0 },
            new double[] { 1.0, 0.2, 0.2, 1.0 });
        _dotTexture = new LoadedTexture(_capi, _capi.Gui.LoadCairoTexture(surface, false), size / 2, size / 2);
        ctx.Dispose();
        ((Surface)surface).Dispose();
    }

    private void RebuildComponents()
    {
        DisposeComponents();
        if (_currentRecords.Count == 0 || _dotTexture == null) return;

        var timestamps = _currentRecords.Select(r => r.Timestamp).Distinct().OrderBy(t => t).ToList();
        if (_sliderValue >= timestamps.Count) return;

        var targetTimestamp = timestamps[_sliderValue];
        var filtered = _currentRecords.Where(r => r.Timestamp == targetTimestamp).ToList();

        _capi?.Logger.Debug($"[MapLayer] Rebuilding: slider={_sliderValue}, timestamp={targetTimestamp}, " +
                            $"showing {filtered.Count} positions");

        foreach (var rec in filtered)
        {
            var pos = new Vec3d(rec.X, rec.Y, rec.Z);
            _components.Add(new PositionMapComponent(_capi, _dotTexture, pos, rec.PlayerUid, rec.Timestamp));
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
