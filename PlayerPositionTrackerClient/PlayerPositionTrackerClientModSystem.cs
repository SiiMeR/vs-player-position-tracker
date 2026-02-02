using System;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.GameContent;

namespace PlayerPositionTracker;

public class PlayerPositionTrackerClientModSystem : ModSystem
{
    private const string ChannelName = "playerpositiontracker";
    private IClientNetworkChannel _clientChannel;

    public event Action<PositionDataResponse> OnResponseReceived;

    public override void Start(ICoreAPI api)
    {
        var mapManager = api.ModLoader.GetModSystem<WorldMapManager>();
        mapManager.RegisterMapLayer<PlayerPositionMapLayer>("playerpositiontracker", 3.0);
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

    private void OnResponseFromServer(PositionDataResponse response)
    {
        OnResponseReceived?.Invoke(response);
    }
}
