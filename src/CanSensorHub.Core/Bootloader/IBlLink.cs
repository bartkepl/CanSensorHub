namespace CanSensorHub.Core.Bootloader;

/// <summary>Bootloader opcodes (OBJ field), identical across MPCC_BL and MPSWP_BL (1:1 ported firmware).</summary>
public static class BlOpcodes
{
    public const byte Hello = 0x70;
    public const byte Erase = 0x71;
    public const byte ProgStart = 0x72;
    public const byte ProgData = 0x73;
    public const byte ProgEnd = 0x74;
    public const byte Verify = 0x75;
    public const byte Go = 0x76;
    public const byte ProgBlockUart = 0x78;
}

public enum BlStatus : byte
{
    Ok = 0x00,
    BadParam = 0x02,
    Range = 0x03,
    Crc = 0x09,
    Flash = 0x20,
    Seq = 0x21,
    State = 0x22,
    NoApp = 0x24,
}

/// <summary>Thrown when the bootloader replies with a non-OK status, or doesn't reply within the timeout.</summary>
public sealed class BootloaderException(string message) : Exception(message);

/// <summary>Transport-agnostic link to a device sitting in its CAN/UART bootloader (see MPCC_BL/MPSWP_BL PROTOCOL.md).</summary>
public interface IBlLink : IDisposable
{
    /// <summary>Sends a request and awaits the matching response payload (bytes after opcode/status framing is stripped). Null on timeout.</summary>
    Task<byte[]?> RequestAsync(byte opcode, byte arg, ReadOnlyMemory<byte> payload, TimeSpan timeout, CancellationToken ct);

    /// <summary>Fire-and-forget send (PROG_DATA over CAN carries no response).</summary>
    Task SendNoResponseAsync(byte opcode, byte arg, ReadOnlyMemory<byte> payload, CancellationToken ct);
}
