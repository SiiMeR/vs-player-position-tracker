using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Server;

namespace PlayerPositionTracker;

public class PlayerPositionTrackerServerModSystem : ModSystem
{
    private const string ModConfigFileName = "playerpositiontrackerconfig.json";
    private const string ChannelName = "playerpositiontracker";
    private static readonly HttpClient HttpClient = new();
    private readonly Dictionary<string, List<PlayerPositionRecord>> _positionsByDate = new();
    private readonly HashSet<string> _dirtyDates = new();
    private PlayerPositionTrackerConfig _config;
    private string _directory;
    private ICoreServerAPI _sapi;
    private IServerNetworkChannel _serverChannel;

    public override void StartServerSide(ICoreServerAPI api)
    {
        _sapi = api;
        _directory = Path.Combine(GamePaths.DataPath, "ModData", api.World.SavegameIdentifier, Mod.Info.ModID);
        if (!Directory.Exists(_directory))
        {
            Directory.CreateDirectory(_directory);
        }

        _config = api.LoadModConfig<PlayerPositionTrackerConfig>(ModConfigFileName);
        if (_config == null)
        {
            _config = new PlayerPositionTrackerConfig();
        }

        api.StoreModConfig(_config, ModConfigFileName);

        api.Event.SaveGameLoaded += LoadFromDisk;
        api.Event.GameWorldSave += SaveToDisk;

        _serverChannel = api.Network.RegisterChannel(ChannelName)
            .RegisterMessageType<PositionDataRequest>()
            .RegisterMessageType<PositionDataResponse>()
            .SetMessageHandler<PositionDataRequest>(OnDateRequestFromClient);

        api.Event.RegisterGameTickListener(_ =>
        {
            var now = DateTime.UtcNow.ToString("o");
            var dateKey = DateTime.UtcNow.ToString("yyyy-MM-dd");

            var records = api.World.AllOnlinePlayers
                .Where(player => (player as IServerPlayer)?.ConnectionState == EnumClientState.Playing)
                .Where(player => player?.Entity?.SidedPos != null && !string.IsNullOrEmpty(player?.PlayerUID))
                .Select(player => new PlayerPositionRecord
                {
                    Timestamp = now,
                    PlayerUid = player.PlayerUID,
                    X = Math.Round(player.Entity.SidedPos.X, 1),
                    Y = Math.Round(player.Entity.SidedPos.Y, 1),
                    Z = Math.Round(player.Entity.SidedPos.Z, 1),
                    Yaw = player.Entity.SidedPos.Yaw
                })
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
            _dirtyDates.Add(dateKey);
        }, _config.PositionUpdateIntervalSeconds * 1000);
    }

    private List<string> GetAvailableDates()
    {
        return _positionsByDate.Keys.OrderBy(k => k).ToList();
    }

    private List<PlayerPositionRecord> GetRecordsForDate(string date)
    {
        return _positionsByDate.TryGetValue(date, out var list) ? list : new List<PlayerPositionRecord>();
    }

    private void OnDateRequestFromClient(IServerPlayer fromPlayer, PositionDataRequest request)
    {
        if (!IsAuthorized(fromPlayer))
        {
            Mod.Logger.Warning(
                $"[PlayerPositionTracker] Unauthorized position data request from {fromPlayer.PlayerName}");
            return;
        }

        var date = request?.Date;
        var dates = GetAvailableDates();
        var records = !string.IsNullOrEmpty(date) ? GetRecordsForDate(date) : new List<PlayerPositionRecord>();

        var playerNames = new Dictionary<string, string>();
        foreach (var uid in records.Select(r => r.PlayerUid).Distinct())
        {
            var data = _sapi.PlayerData.GetPlayerDataByUid(uid);
            if (data != null)
            {
                playerNames[uid] = data.LastKnownPlayername;
            }
        }

        var dateInfo = string.IsNullOrEmpty(date) ? "available dates" : $"date {date}";
        var playerFilter = request?.PlayerFilter;
        string filterInfo;
        if (string.IsNullOrEmpty(playerFilter) || playerFilter == "__all__")
        {
            filterInfo = "all players";
        }
        else
        {
            var playerData = _sapi.PlayerData.GetPlayerDataByUid(playerFilter);
            filterInfo = playerData != null ? $"player {playerData.LastKnownPlayername}" : $"player {playerFilter}";
        }

        var auditMessage = $"[PlayerPositionTracker] {fromPlayer.PlayerName} requested {dateInfo} for {filterInfo}";
        _sapi.Logger.Audit(auditMessage);
        SendDiscordAudit(auditMessage);

        _serverChannel.SendPacket(new PositionDataResponse
        {
            AvailableDates = dates,
            Records = records,
            PlayerNames = playerNames
        }, fromPlayer);
    }

    private static bool IsAuthorized(IPlayer player)
    {
        return player.Role?.Code == "admin" &&
               player.WorldData?.CurrentGameMode == EnumGameMode.Creative;
    }

    private void SendDiscordAudit(string message)
    {
        if (string.IsNullOrEmpty(_config?.DiscordBotToken) || string.IsNullOrEmpty(_config?.DiscordChannelId))
        {
            return;
        }

        try
        {
            var url = $"https://discord.com/api/v10/channels/{_config.DiscordChannelId}/messages";
            var json = $"{{\"content\":\"{message.Replace("\"", "\\\"").Replace("\n", "\\n")}\"}}";
            var request = new HttpRequestMessage(HttpMethod.Post, url);
            request.Headers.Add("Authorization", $"Bot {_config.DiscordBotToken}");
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
            HttpClient.SendAsync(request);
        }
        catch (Exception e)
        {
            _sapi.Logger.Warning($"[PlayerPositionTracker] Failed to send Discord audit: {e.Message}");
        }
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
                _sapi.Logger.Error($"[PlayerPositionTracker] Failed to load position data from {file}: {e.Message}");
            }
        }
    }

    private void SaveToDisk()
    {
        foreach (var dateKey in _dirtyDates)
        {
            if (!_positionsByDate.TryGetValue(dateKey, out var records)) continue;
            var path = Path.Combine(_directory, $"playerpositions-{dateKey}.json");
            File.WriteAllText(path, JsonUtil.ToString(records));
        }
        _dirtyDates.Clear();
    }
}

public class PlayerPositionTrackerConfig
{
    public int PositionUpdateIntervalSeconds { get; set; } = 60;
    public string DiscordBotToken { get; set; } = "";
    public string DiscordChannelId { get; set; } = "";
}
