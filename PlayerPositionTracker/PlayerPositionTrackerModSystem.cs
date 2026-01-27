using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Server;

namespace PlayerPositionTracker;

public class PlayerPositionTrackerModSystem : ModSystem
{
    private const string ModConfigFileName = "playerpositiontrackerconfig.json";
    private string _directory;
    private readonly Dictionary<string, List<PlayerPositionRecord>> _positionsByDate = new();

    private ICoreServerAPI _sapi;

    public override void StartServerSide(ICoreServerAPI api)
    {
        _sapi = api;
        _directory = Path.Combine(GamePaths.DataPath, "ModData", api.World.SavegameIdentifier, Mod.Info.ModID);
        if (!Directory.Exists(_directory))
        {
            Directory.CreateDirectory(_directory);
        }

        var config = api.LoadModConfig<PlayerPositionTrackerConfig>(ModConfigFileName);
        if (config == null)
        {
            config = new PlayerPositionTrackerConfig();
        }

        api.StoreModConfig(config, ModConfigFileName);


        api.Event.SaveGameLoaded += LoadFromDisk;
        api.Event.GameWorldSave += SaveToDisk;

        api.Event.RegisterGameTickListener(_ =>
        {
            var now = DateTime.UtcNow.ToString("o");
            var dateKey = DateTime.UtcNow.ToString("yyyy-MM-dd");

            var records = api.World.AllOnlinePlayers
                .Select(player =>
                {
                    if (player.Entity?.SidedPos == null || string.IsNullOrEmpty(player.PlayerUID))
                    {
                        return null;
                    }

                    return new PlayerPositionRecord
                    {
                        Timestamp = now,
                        PlayerUid = player.PlayerUID,
                        X = Math.Round(player.Entity.SidedPos.X, 1),
                        Y = Math.Round(player.Entity.SidedPos.Y, 1),
                        Z = Math.Round(player.Entity.SidedPos.Z, 1)
                    };
                })
                .Where(rec => rec != null)
                .ToList();

            if (records.Count == 0)
            {
                return;
            }

            if (!_positionsByDate.TryGetValue(dateKey, out var list))
            {
                list = new List<PlayerPositionRecord>();
                _positionsByDate[dateKey] = list;
            }

            list.AddRange(records);
        }, config.PositionUpdateIntervalSeconds * 1000);
    }

    public List<string> GetAvailableDates()
    {
        return _positionsByDate.Keys.OrderBy(k => k).ToList();
    }

    public List<PlayerPositionRecord> GetRecordsForDate(string date)
    {
        return _positionsByDate.TryGetValue(date, out var list) ? list : new List<PlayerPositionRecord>();
    }

    private void LoadFromDisk()
    {
        _positionsByDate.Clear();
        foreach (var file in Directory.GetFiles(_directory, "playerpositions-*.json"))
        {
            var dateKey = Path.GetFileNameWithoutExtension(file).Replace("playerpositions-", "");
            try
            {
                var json = File.ReadAllText(file);
                var records = JsonUtil.FromString<List<PlayerPositionRecord>>(json);
                if (records != null)
                {
                    _positionsByDate[dateKey] = records;
                }
            }
            catch (Exception e)
            {
                _sapi.Logger.Error($"Failed to load position data from {file}: {e.Message}");
            }
        }

        _sapi.Logger.Debug($"Loaded position data for {_positionsByDate.Count} days.");
    }

    private void SaveToDisk()
    {
        foreach (var (dateKey, records) in _positionsByDate)
        {
            var path = Path.Combine(_directory, $"playerpositions-{dateKey}.json");
            File.WriteAllText(path, JsonUtil.ToString(records));
        }

        _sapi.Logger.Debug($"Saved position data for {_positionsByDate.Count} days.");
    }
}

public class PlayerPositionTrackerConfig
{
    public int PositionUpdateIntervalSeconds { get; set; } = 60;
}

public class PlayerPositionRecord
{
    public string Timestamp { get; set; }
    public string PlayerUid { get; set; }
    public double X { get; set; }
    public double Y { get; set; }
    public double Z { get; set; }
}