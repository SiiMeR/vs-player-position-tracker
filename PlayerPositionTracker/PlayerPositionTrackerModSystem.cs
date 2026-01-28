using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using ProtoBuf;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

namespace PlayerPositionTracker;

public class PlayerPositionTrackerModSystem : ModSystem
{
    private const string ModConfigFileName = "playerpositiontrackerconfig.json";
    private const string ChannelName = "playerpositiontracker";
    private string _directory;
    private readonly Dictionary<string, List<PlayerPositionRecord>> _positionsByDate = new();
    private static readonly HttpClient HttpClient = new();

    private ICoreServerAPI _sapi;
    private IServerNetworkChannel _serverChannel;
    private IClientNetworkChannel _clientChannel;
    private PlayerPositionTrackerConfig _config;

    public event Action<PositionDataResponse> OnResponseReceived;

    public override void Start(ICoreAPI api)
    {
        var mapManager = api.ModLoader.GetModSystem<WorldMapManager>();
        mapManager.RegisterMapLayer<PlayerPositionMapLayer>("playerpositiontracker", 0.75);
    }

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
                        Z = Math.Round(player.Entity.SidedPos.Z, 1),
                        Yaw = player.Entity.SidedPos.Yaw
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
        }, _config.PositionUpdateIntervalSeconds * 1000);
    }

    public override void StartClientSide(ICoreClientAPI api)
    {
        _clientChannel = api.Network.RegisterChannel(ChannelName)
            .RegisterMessageType<PositionDataRequest>()
            .RegisterMessageType<PositionDataResponse>()
            .SetMessageHandler<PositionDataResponse>(OnResponseFromServer);

        WorldMapPatches.Init(api, this);
    }

    public void RequestDateData(string date, string playerFilter = null)
    {
        _clientChannel?.SendPacket(new PositionDataRequest { Date = date ?? "", PlayerFilter = playerFilter });
    }

    public List<string> GetAvailableDates()
    {
        return _positionsByDate.Keys.OrderBy(k => k).ToList();
    }

    public List<PlayerPositionRecord> GetRecordsForDate(string date)
    {
        return _positionsByDate.TryGetValue(date, out var list) ? list : new List<PlayerPositionRecord>();
    }

    private void OnDateRequestFromClient(IServerPlayer fromPlayer, PositionDataRequest request)
    {
        if (!IsAuthorized(fromPlayer))
        {
            Mod.Logger.Warning($"[PlayerPositionTracker] Unauthorized position data request from {fromPlayer.PlayerName}");
            return;
        }

        var date = request?.Date;
        var dates = GetAvailableDates();
        var records = !string.IsNullOrEmpty(date) ? GetRecordsForDate(date) : new List<PlayerPositionRecord>();

        var playerNames = new Dictionary<string, string>();
        foreach (var uid in records.Select(r => r.PlayerUid).Distinct())
        {
            var data = _sapi.PlayerData.GetPlayerDataByUid(uid);
            if (data != null) playerNames[uid] = data.LastKnownPlayername;
        }

        var dateInfo = string.IsNullOrEmpty(date) ? "available dates" : $"date {date}";
        var playerFilter = request?.PlayerFilter;
        string filterInfo;
        if (string.IsNullOrEmpty(playerFilter) || playerFilter == "__all__")
            filterInfo = "all players"; 
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
            return;

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

    private void OnResponseFromServer(PositionDataResponse response)
    {
        OnResponseReceived?.Invoke(response);
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
        foreach (var (dateKey, records) in _positionsByDate)
        {
            var path = Path.Combine(_directory, $"playerpositions-{dateKey}.json");
            File.WriteAllText(path, JsonUtil.ToString(records));
        }

        _sapi.Logger.Debug($"[PlayerPositionTracker] Saved position data for {_positionsByDate.Count} days.");
    }
}

public class PlayerPositionTrackerConfig
{
    public int PositionUpdateIntervalSeconds { get; set; } = 60;
    public string DiscordBotToken { get; set; } = "";
    public string DiscordChannelId { get; set; } = "";
}

[ProtoContract]
public class PlayerPositionRecord
{
    [ProtoMember(1)]
    public string Timestamp { get; set; }

    [ProtoMember(2)]
    public string PlayerUid { get; set; }

    [ProtoMember(3)]
    public double X { get; set; }

    [ProtoMember(4)]
    public double Y { get; set; }

    [ProtoMember(5)]
    public double Z { get; set; }

    [ProtoMember(6)]
    public float Yaw { get; set; }
}

[ProtoContract]
public class PositionDataRequest
{
    [ProtoMember(1)]
    public string Date { get; set; }

    [ProtoMember(2)]
    public string PlayerFilter { get; set; }
}

[ProtoContract]
public class PositionDataResponse
{
    [ProtoMember(1)]
    public List<string> AvailableDates { get; set; }

    [ProtoMember(2)]
    public List<PlayerPositionRecord> Records { get; set; }

    [ProtoMember(3)]
    public Dictionary<string, string> PlayerNames { get; set; }
}

