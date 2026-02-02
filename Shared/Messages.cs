using System.Collections.Generic;
using ProtoBuf;

namespace PlayerPositionTracker;

[ProtoContract]
public class PlayerPositionRecord
{
    [ProtoMember(1)] public string Timestamp { get; set; }
    [ProtoMember(2)] public string PlayerUid { get; set; }
    [ProtoMember(3)] public double X { get; set; }
    [ProtoMember(4)] public double Y { get; set; }
    [ProtoMember(5)] public double Z { get; set; }
    [ProtoMember(6)] public float Yaw { get; set; }
}

[ProtoContract]
public class PositionDataRequest
{
    [ProtoMember(1)] public string Date { get; set; }
    [ProtoMember(2)] public string PlayerFilter { get; set; }
}

[ProtoContract]
public class PositionDataResponse
{
    [ProtoMember(1)] public List<string> AvailableDates { get; set; }
    [ProtoMember(2)] public List<PlayerPositionRecord> Records { get; set; }
    [ProtoMember(3)] public Dictionary<string, string> PlayerNames { get; set; }
}
