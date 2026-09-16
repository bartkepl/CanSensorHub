using CanSensorHub.Core.Can;

namespace CanSensorHub.Core.Protocol;

/// <summary>Envelope-level decode of any frame, independent of module-specific tables.</summary>
public readonly record struct ResponseMessage(byte Node, byte Opcode, byte Arg, StatusCode Status, byte[] Data);

public readonly record struct StreamSegment(byte Node, byte Obj, byte Arg, int Index, bool Last, byte[] Payload);

public readonly record struct EventMessage(byte Node, byte Source, byte Code, byte[] Data);

public readonly record struct RequestMessage(byte Node, byte Opcode, byte Arg, byte[] Data);

/// <summary>
/// Raw telemetry payload layout (max 8 bytes), identical for every module:
///   [0] seq u8, [1] flags u8 (bit0=valid, bits[4:1]=source count, bit5=clamped, bit6=range warn,
///   bit7=range err dropped), [2:5] value i32 LE, [6:7] qual u16 LE.
/// RangeWarn/RangeErrDropped are aggregated (OR'd) across every sensor contributing to this channel's
/// fusion — see MPSWP's LPL/UPL (process) / LTL/UTL (transducer) limits, modelled on HART cmd 48.
/// </summary>
public readonly record struct TelemetryRaw(byte Node, byte Channel, byte Seq, int Value, byte Flags, ushort Qual)
{
    public bool Valid => (Flags & 0x01) != 0;
    public bool Clamped => (Flags & 0x20) != 0;
    public bool RangeWarn => (Flags & 0x40) != 0;
    public bool RangeErrDropped => (Flags & 0x80) != 0;
    public int SourceCount => (Flags >> 1) & 0x0F;

    public static TelemetryRaw Parse(byte node, byte channel, ReadOnlySpan<byte> data)
    {
        byte seq = data.Length > 0 ? data[0] : (byte)0;
        byte flags = data.Length > 1 ? data[1] : (byte)0;
        int value = data.Length >= 6 ? BitConverter.ToInt32(data.Slice(2, 4)) : 0;
        ushort qual = data.Length >= 8 ? BitConverter.ToUInt16(data.Slice(6, 2)) : (ushort)0;
        return new TelemetryRaw(node, channel, seq, value, flags, qual);
    }

    public static byte[] Build(byte seq, int value, int nSensors, bool valid, bool clamped, ushort qual,
        bool rangeWarn = false, bool rangeErrDropped = false)
    {
        byte flags = 0;
        if (valid) flags |= 0x01;
        flags |= (byte)((nSensors & 0x0F) << 1);
        if (clamped) flags |= 0x20;
        if (rangeWarn) flags |= 0x40;
        if (rangeErrDropped) flags |= 0x80;
        var buf = new byte[8];
        buf[0] = seq;
        buf[1] = flags;
        BitConverter.GetBytes(value).CopyTo(buf, 2);
        BitConverter.GetBytes(qual).CopyTo(buf, 6);
        return buf;
    }
}

/// <summary>
/// Subsystem status snapshot (GET_STATUS response / CRITICAL_ALARM payload) — identical 7-byte layout
/// for every module (modelled on HART command 48):
///   [0] SYS u8, [1] ERR u8, [2:3] PRESENT u16 LE, [4:5] FAULT u16 LE, [6] NHEALTHY u8.
/// PRESENT/FAULT are bitmaps positional against each module's own sensor id ordering.
/// </summary>
public readonly record struct DeviceStatusReport(byte Sys, byte Err, ushort Present, ushort Fault, byte NHealthy)
{
    public bool Critical => (Err & 0x01) != 0;
    public DeviceSysFlags SysFlags => (DeviceSysFlags)Sys;
    public DeviceErrFlags ErrFlags => (DeviceErrFlags)Err;

    public static DeviceStatusReport Parse(ReadOnlySpan<byte> data)
    {
        byte sys = data.Length > 0 ? data[0] : (byte)0;
        byte err = data.Length > 1 ? data[1] : (byte)0;
        ushort present = data.Length >= 4 ? BitConverter.ToUInt16(data.Slice(2, 2)) : (ushort)0;
        ushort fault = data.Length >= 6 ? BitConverter.ToUInt16(data.Slice(4, 2)) : (ushort)0;
        byte nHealthy = data.Length > 6 ? data[6] : (byte)0;
        return new DeviceStatusReport(sys, err, present, fault, nHealthy);
    }
}

[Flags]
public enum DeviceSysFlags : byte
{
    None = 0,
    LseActive = 1 << 0,
    RtcOk = 1 << 1,
    CanOk = 1 << 2,
    I2cOk = 1 << 3,
    AdcOk = 1 << 4,
    FpuOk = 1 << 5,
    ConfigOk = 1 << 6,
    SensorsOk = 1 << 7,
}

[Flags]
public enum DeviceErrFlags : byte
{
    None = 0,
    Critical = 1 << 0,
    SensorFault = 1 << 1,
    NoSensors = 1 << 2,
    WdtReset = 1 << 3,
    NvmError = 1 << 4,
    CanBusoff = 1 << 5,
    I2cRecovered = 1 << 6,
    VbatLow = 1 << 7,
}

/// <summary>Stream segment header (byte 0 of a FUNC=STREAM frame): bit7 = last, bits[6:0] = index.</summary>
public static class StreamHeaderBits
{
    public const byte LastBit = 0x80;
    public const byte IndexMask = 0x7F;

    public static byte Build(int index, bool last) => (byte)(((index & IndexMask) & 0xFF) | (last ? LastBit : 0));
}

/// <summary>Reassembles segmented FUNC=STREAM transfers keyed by (node, obj, arg).</summary>
public sealed class StreamAssembler
{
    private readonly Dictionary<(byte Node, byte Obj, byte Arg), Dictionary<int, byte[]>> _buffers = new();

    /// <summary>Feeds one segment; returns the concatenated payload once the "last" segment arrives, else null.</summary>
    public byte[]? Push(StreamSegment segment)
    {
        var key = (segment.Node, segment.Obj, segment.Arg);
        if (!_buffers.TryGetValue(key, out var segs))
        {
            segs = new Dictionary<int, byte[]>();
            _buffers[key] = segs;
        }
        segs[segment.Index] = segment.Payload;

        if (!segment.Last) return null;

        _buffers.Remove(key);
        var total = 0;
        for (int i = 0; i <= segment.Index; i++)
            total += segs.TryGetValue(i, out var p) ? p.Length : 0;
        var result = new byte[total];
        var offset = 0;
        for (int i = 0; i <= segment.Index; i++)
        {
            if (!segs.TryGetValue(i, out var p)) continue;
            p.CopyTo(result, offset);
            offset += p.Length;
        }
        return result;
    }
}

/// <summary>
/// Envelope-only decoder: understands FUNC/NODE/OBJ/ARG framing and the common payload shapes, but has
/// no knowledge of a specific module's channel/sensor/param tables. Used by the shell's raw bus monitor
/// and as the base every module's own typed codec builds on.
/// </summary>
public static class EnvelopeCodec
{
    public static CanFrame BuildRequest(byte node, byte opcode, byte arg = 0, ReadOnlySpan<byte> data = default)
        => Build(CanFunc.Request, node, opcode, arg, data);

    public static CanFrame BuildResponse(byte node, byte opcode, byte arg, StatusCode status, ReadOnlySpan<byte> data = default)
    {
        var buf = new byte[1 + data.Length];
        buf[0] = (byte)status;
        data.CopyTo(buf.AsSpan(1));
        return Build(CanFunc.Response, node, opcode, arg, buf);
    }

    public static CanFrame BuildStream(byte node, byte obj, byte arg, int index, bool last, ReadOnlySpan<byte> payload)
    {
        var buf = new byte[1 + payload.Length];
        buf[0] = StreamHeaderBits.Build(index, last);
        payload.CopyTo(buf.AsSpan(1));
        return Build(CanFunc.Stream, node, obj, arg, buf);
    }

    public static CanFrame BuildEvent(byte node, byte source, byte code, ReadOnlySpan<byte> data = default)
        => Build(CanFunc.Event, node, source, code, data);

    public static CanFrame BuildTelemetry(byte node, byte channel, ReadOnlySpan<byte> payload)
        => Build(CanFunc.Telemetry, node, channel, 0, payload);

    private static CanFrame Build(CanFunc func, byte node, byte obj, byte arg, ReadOnlySpan<byte> data)
    {
        var id = new CanId((byte)func, node, obj, arg).Pack();
        return new CanFrame(id, data);
    }

    public static StreamSegment ParseStream(CanId id, byte[] data)
    {
        byte hdr = data.Length > 0 ? data[0] : (byte)0;
        var payload = data.Length > 1 ? data[1..] : Array.Empty<byte>();
        return new StreamSegment(id.Node, id.Obj, id.Arg, hdr & StreamHeaderBits.IndexMask, (hdr & StreamHeaderBits.LastBit) != 0, payload);
    }

    public static ResponseMessage ParseResponse(CanId id, byte[] data)
    {
        var status = data.Length > 0 ? (StatusCode)data[0] : StatusCode.Ok;
        var rest = data.Length > 1 ? data[1..] : Array.Empty<byte>();
        return new ResponseMessage(id.Node, id.Obj, id.Arg, status, rest);
    }

    public static EventMessage ParseEvent(CanId id, byte[] data) => new(id.Node, id.Obj, id.Arg, data);

    public static RequestMessage ParseRequest(CanId id, byte[] data) => new(id.Node, id.Obj, id.Arg, data);

    public static TelemetryRaw ParseTelemetry(CanId id, byte[] data) => TelemetryRaw.Parse(id.Node, id.Obj, data);
}
