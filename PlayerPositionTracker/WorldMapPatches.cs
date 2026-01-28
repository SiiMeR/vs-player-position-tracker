using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.GameContent;

namespace PlayerPositionTracker;

public static class WorldMapPatches
{
    private static ICoreClientAPI _capi;
    private static PlayerPositionTrackerModSystem _modSystem;
    private static FieldInfo _tabnamesField;

    public static void Init(ICoreClientAPI capi, PlayerPositionTrackerModSystem modSystem)
    {
        _capi = capi;
        _modSystem = modSystem;

        _tabnamesField = typeof(GuiDialogWorldMap).GetField("tabnames", BindingFlags.NonPublic | BindingFlags.Instance);

        var harmony = new Harmony("playerpositiontracker");

        var getTabsOrdered = typeof(WorldMapManager).GetMethod("getTabsOrdered", BindingFlags.NonPublic | BindingFlags.Instance);
        var getTabsOrderedPostfix = typeof(WorldMapPatches).GetMethod(nameof(Postfix_getTabsOrdered), BindingFlags.Public | BindingFlags.Static);
        harmony.Patch(getTabsOrdered, postfix: new HarmonyMethod(getTabsOrderedPostfix));

        var toggleMap = typeof(WorldMapManager).GetMethod(nameof(WorldMapManager.ToggleMap), BindingFlags.Public | BindingFlags.Instance);
        var toggleMapPrefix = typeof(WorldMapPatches).GetMethod(nameof(Prefix_ToggleMap), BindingFlags.Public | BindingFlags.Static);
        harmony.Patch(toggleMap, prefix: new HarmonyMethod(toggleMapPrefix));

        var onTabClicked = typeof(GuiDialogWorldMap).GetMethod("OnTabClicked", BindingFlags.NonPublic | BindingFlags.Instance);
        var onTabClickedPrefix = typeof(WorldMapPatches).GetMethod(nameof(Prefix_OnTabClicked), BindingFlags.Public | BindingFlags.Static);
        harmony.Patch(onTabClicked, prefix: new HarmonyMethod(onTabClickedPrefix));
    }

    public static void Postfix_getTabsOrdered(ref List<string> __result)
    {
        if (_capi == null || IsClientAuthorized()) return;
        __result.Remove("playerpositiontracker");
    }

    public static void Prefix_ToggleMap(WorldMapManager __instance)
    {
        if (__instance.worldMapDlg == null || __instance.worldMapDlg.IsOpened()) return;
        __instance.worldMapDlg.Dispose();
        __instance.worldMapDlg = null;
    }

    public static bool Prefix_OnTabClicked(GuiDialogWorldMap __instance, int arg1, GuiTab tab)
    {
        if (_tabnamesField == null) return true;
        var tabnames = _tabnamesField.GetValue(__instance) as List<string>;
        if (tabnames == null || arg1 >= tabnames.Count) return true;

        var tabCode = tabnames[arg1];
        if (tabCode != "playerpositiontracker") return true;
        if (!tab.Active) return true;

        var mapManager = _capi.ModLoader.GetModSystem<WorldMapManager>();
        var layer = mapManager.MapLayers.FirstOrDefault(l => l.LayerGroupCode == "playerpositiontracker");
        if (layer == null) return true;

        tab.Active = false;
        layer.Active = false;

        ShowConfirmationDialog(__instance, layer, tab);
        return false;
    }

    private static void ShowConfirmationDialog(GuiDialogWorldMap mapDlg, MapLayer layer, GuiTab tab)
    {
        var dlg = new GuiDialogConfirm(_capi,
            "The use of this layer is audited. Are you sure you want to continue?",
            confirmed =>
            {
                if (!confirmed) return;
                tab.Active = true;
                layer.Active = true;
                _modSystem.RequestDateData("");
                var key = "worldmap-layer-playerpositiontracker";
                if (mapDlg.Composers.ContainsKey(key))
                {
                    mapDlg.Composers[key].Enabled = true;
                }
            });
        dlg.TryOpen();
    }

    private static bool IsClientAuthorized()
    {
        var player = _capi?.World?.Player;
        return player?.Role?.Code == "admin" &&
               player.WorldData?.CurrentGameMode == EnumGameMode.Creative;
    }
}
