namespace CanSensorHub.Core.Protocol;

public enum ParamValueType { U8, U16, U32, I16, I32, F32 }

public enum ParamAccess { ReadOnly, ReadWrite }

/// <summary>Where the firmware keeps the value: survives SAVE_CONFIG (non-volatile/Flash) or RAM-only (lost on reset).</summary>
public enum ParamStorage { Ram, NonVolatile }

/// <summary>Hints the WPF params view how to render/edit a value — chosen by hand from each protocol.yaml's description text.</summary>
public enum ParamControlKind
{
    /// <summary>0/1 boolean flag.</summary>
    Checkbox,
    /// <summary>Small closed set of named values (mode selectors).</summary>
    ComboBox,
    /// <summary>Free-form bounded number (integer or decimal, per <see cref="ParamDescriptor.Type"/>).</summary>
    Numeric,
}

public readonly record struct ParamOption(int Value, string Label);

/// <summary>One entry of a module's configuration parameter registry (WRITE_PARAM/READ_PARAM, ARG = Id).</summary>
public sealed class ParamDescriptor
{
    public required byte Id { get; init; }
    public required string Name { get; init; }
    public required ParamValueType Type { get; init; }
    public ParamAccess Access { get; init; } = ParamAccess.ReadWrite;
    public ParamStorage Storage { get; init; } = ParamStorage.NonVolatile;
    public required double Min { get; init; }
    public required double Max { get; init; }
    public required double Default { get; init; }
    public string Unit { get; init; } = "-";
    public required string Description { get; init; }
    public ParamControlKind Control { get; init; } = ParamControlKind.Numeric;
    public IReadOnlyList<ParamOption>? Options { get; init; }
    /// <summary>Decimal places to show for f32 numeric params; ignored for integer types (always shown as plain integers).</summary>
    public int DecimalPlaces { get; init; } = 3;
    /// <summary>Optional grouping label for the params view (e.g. "System", "Kalibracja ADC0").</summary>
    public string Group { get; init; } = "Ogólne";
    /// <summary>True for byte-address-like integer params (e.g. NODE_ID) that read more naturally as hex than decimal.</summary>
    public bool DisplayHex { get; init; }
    /// <summary>
    /// Parametr kalibracyjny (flaga <c>calibration</c> w <c>device.yaml</c>): zapis odrzucany przez węzeł
    /// statusem <see cref="StatusCode.ErrReadonly"/>, dopóki parametr uniwersalny <c>CAL_LOCK</c> jest ustawiony.
    /// </summary>
    public bool Calibration { get; init; }

    public bool IsReadOnly => Access == ParamAccess.ReadOnly;
}

public static class ParamCodec
{
    public static int ByteSize(ParamValueType type) => type switch
    {
        ParamValueType.U8 => 1,
        ParamValueType.U16 or ParamValueType.I16 => 2,
        ParamValueType.U32 or ParamValueType.I32 or ParamValueType.F32 => 4,
        _ => throw new ArgumentOutOfRangeException(nameof(type)),
    };

    public static byte[] Encode(ParamValueType type, double value) => type switch
    {
        ParamValueType.U8 => [(byte)Math.Clamp(Math.Round(value), 0, 255)],
        ParamValueType.U16 => BitConverter.GetBytes((ushort)Math.Clamp(Math.Round(value), 0, 65535)),
        ParamValueType.U32 => BitConverter.GetBytes((uint)Math.Clamp(Math.Round(value), 0, uint.MaxValue)),
        ParamValueType.I16 => BitConverter.GetBytes((short)Math.Clamp(Math.Round(value), short.MinValue, short.MaxValue)),
        ParamValueType.I32 => BitConverter.GetBytes((int)Math.Clamp(Math.Round(value), int.MinValue, int.MaxValue)),
        ParamValueType.F32 => BitConverter.GetBytes((float)value),
        _ => throw new ArgumentOutOfRangeException(nameof(type)),
    };

    public static double Decode(ParamValueType type, ReadOnlySpan<byte> data)
    {
        var size = ByteSize(type);
        if (data.Length < size) return 0;
        return type switch
        {
            ParamValueType.U8 => data[0],
            ParamValueType.U16 => BitConverter.ToUInt16(data),
            ParamValueType.U32 => BitConverter.ToUInt32(data),
            ParamValueType.I16 => BitConverter.ToInt16(data),
            ParamValueType.I32 => BitConverter.ToInt32(data),
            ParamValueType.F32 => BitConverter.ToSingle(data),
            _ => throw new ArgumentOutOfRangeException(nameof(type)),
        };
    }
}
