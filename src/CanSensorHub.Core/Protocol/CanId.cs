namespace CanSensorHub.Core.Protocol;

/// <summary>
/// The 29-bit extended CAN identifier layout shared, bit-for-bit, by MPCC and MPSWP:
///   bits [28:24] FUNC (5b) - message class, lower value = higher bus priority
///   bits [23:16] NODE (8b) - node address (0xFF = broadcast, 0x00 = unconfigured)
///   bits [15:8]  OBJ  (8b) - object: telemetry channel / opcode / event source
///   bits [7:0]   ARG  (8b) - argument: sensor id / parameter id / event code
/// </summary>
public readonly record struct CanId(byte Func, byte Node, byte Obj, byte Arg)
{
    public const byte NodeBroadcast = 0xFF;
    public const byte NodeUnconfigured = 0x00;

    public static CanId Unpack(uint id) => new(
        Func: (byte)((id >> 24) & 0x1F),
        Node: (byte)((id >> 16) & 0xFF),
        Obj: (byte)((id >> 8) & 0xFF),
        Arg: (byte)(id & 0xFF));

    public uint Pack() => ((uint)(Func & 0x1F) << 24) | ((uint)Node << 16) | ((uint)Obj << 8) | Arg;
}

/// <summary>FUNC values — identical across every module on the bus.</summary>
public enum CanFunc : byte
{
    Event = 0x01,
    Request = 0x08,
    Response = 0x09,
    Stream = 0x0A,
    Telemetry = 0x10,
}

/// <summary>Status byte (RESPONSE frame, byte 0) — identical set of codes across every module.</summary>
public enum StatusCode : byte
{
    Ok = 0x00,
    ErrUnknownCmd = 0x01,
    ErrBadParam = 0x02,
    ErrOutOfRange = 0x03,
    ErrSensorAbsent = 0x04,
    ErrSensorFault = 0x05,
    ErrBusy = 0x06,
    ErrReadonly = 0x07,
    ErrNotReady = 0x08,
    ErrCrc = 0x09,
}
