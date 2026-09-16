using CanSensorHub.Core.Protocol;

namespace CanSensorHub.Core.Can;

/// <summary>One row of the shell's raw bus monitor — envelope-decoded, module-agnostic.</summary>
public sealed class CanLogEntry
{
    public required DateTimeOffset Timestamp { get; init; }
    public required FrameDirection Direction { get; init; }
    public required CanFrame Frame { get; init; }
    public required CanId Id { get; init; }
    public required string FuncName { get; init; }

    public static CanLogEntry Create(CanFrame frame, FrameDirection direction)
    {
        var id = CanId.Unpack(frame.Id);
        var funcName = Enum.IsDefined(typeof(CanFunc), id.Func) ? ((CanFunc)id.Func).ToString() : $"0x{id.Func:X2}";
        return new CanLogEntry
        {
            Timestamp = frame.Timestamp,
            Direction = direction,
            Frame = frame,
            Id = id,
            FuncName = funcName,
        };
    }

    public string DataHex => string.Join(' ', Array.ConvertAll(Frame.Data, b => b.ToString("X2")));
}
