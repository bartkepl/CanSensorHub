namespace CanSensorHub.Core.Can;

/// <summary>
/// Backend-agnostic representation of a classic CAN 2.0 frame (extended 29-bit ID, up to 8 data bytes).
/// </summary>
public readonly struct CanFrame
{
    public const int MaxDataLength = 8;
    public const uint MaxExtendedId = (1u << 29) - 1;

    public uint Id { get; }
    public byte[] Data { get; }
    public bool IsExtended { get; }
    public bool IsRemote { get; }
    public DateTimeOffset Timestamp { get; }

    public CanFrame(uint id, ReadOnlySpan<byte> data, bool isExtended = true, bool isRemote = false, DateTimeOffset? timestamp = null)
    {
        var maxId = isExtended ? MaxExtendedId : 0x7FFu;
        if (id > maxId)
            throw new ArgumentOutOfRangeException(nameof(id), $"CAN id 0x{id:X} exceeds {(isExtended ? "extended (29-bit)" : "standard (11-bit)")} range");
        if (data.Length > MaxDataLength)
            throw new ArgumentOutOfRangeException(nameof(data), "Classic CAN frames carry at most 8 data bytes");

        Id = id;
        Data = data.ToArray();
        IsExtended = isExtended;
        IsRemote = isRemote;
        Timestamp = timestamp ?? DateTimeOffset.Now;
    }

    public override string ToString()
        => $"0x{Id:X8} [{Data.Length}] {string.Join(' ', Array.ConvertAll(Data, b => b.ToString("X2")))}";
}
