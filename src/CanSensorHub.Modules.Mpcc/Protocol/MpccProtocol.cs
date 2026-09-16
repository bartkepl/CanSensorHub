namespace CanSensorHub.Modules.Mpcc.Protocol;

/// <summary>
/// Ported 1:1 from MPCC/protocol/protocol.yaml (the project's single source of truth). MPCC is
/// bit-compatible with MPSWP (identical 29-bit ID layout, FUNC values, status codes, stream/status
/// framing — see CanSensorHub.Core.Protocol) but has its own telemetry channels, sensors, quantities,
/// request opcodes (0x0D+ are MPCC-only extensions) and parameter registry.
/// </summary>
public static class MpccNodes
{
    public const byte Broadcast = 0xFF;
    public const byte Unconfigured = 0x00;
    public const byte Default = 0x10;
}

public static class MpccInfo
{
    public const byte FwVersionMajor = 0;
    public const byte FwVersionMinor = 1;
    public const byte ProtocolVersion = 1;
}

public enum MpccChannel : byte
{
    Temp = 0x01,
    Adc0 = 0x02,
    Adc1 = 0x03,
    Adc2 = 0x04,
    Adc3 = 0x05,
    Adc4 = 0x06,
    Adc5 = 0x07,
    Adc6 = 0x08,
    Vdda = 0x09,
    McuTemp = 0x0A,
}

public sealed record ChannelInfo(string Label, string Unit, double Scale, string Description);

public enum MpccSensor : byte
{
    Sts31 = 0x07,
    Pcf8574 = 0x0B,
    Mcu = 0x0C,
}

public enum MpccQuantity : byte
{
    Temp = 0x01,
    McuTemp = 0x0A,
    Vdda = 0x0B,
    Vbat = 0x0C,
    AdcV = 0x20,
}

public sealed record QuantityInfo(string Label, string Unit, double Scale);

public enum MpccReqOp : byte
{
    Ping = 0x00,
    GetInfo = 0x01,
    ReadAvg = 0x02,
    ReadSensor = 0x03,
    ReadParam = 0x04,
    WriteParam = 0x05,
    ListSensors = 0x06,
    SaveConfig = 0x07,
    LoadDefaults = 0x08,
    GetTime = 0x09,
    SetTime = 0x0A,
    GetStatus = 0x0B,
    Reset = 0x0C,
    GetOutput = 0x0D,
    SetOutput = 0x0E,
    SetLed = 0x0F,
    ReadAdc = 0x10,
    EnterBootloader = 0x11,
}

public enum MpccEventCode : byte
{
    Boot = 0x01,
    Sts31Alert = 0x03,
    SensorFault = 0x04,
    SensorBack = 0x05,
    BusRecovered = 0x06,
    Heartbeat = 0x07,
    SensorIrq = 0x08,
    CriticalAlarm = 0x09,
    Button = 0x0A,
    OutputChanged = 0x0B,
}

public static class MpccEventSource
{
    public const byte System = 0x00;
}

public static class MpccTables
{
    public static readonly IReadOnlyDictionary<MpccChannel, ChannelInfo> Channels = new Dictionary<MpccChannel, ChannelInfo>
    {
        [MpccChannel.Temp] = new("Temperatura", "°C", 0.01, "Temperatura płytki (STS31-DIS)"),
        [MpccChannel.Adc0] = new("ADC0", "V", 0.001, "Wejście ADC0 (dzielnik ~0.6, zakres ~5 V)"),
        [MpccChannel.Adc1] = new("ADC1", "V", 0.001, "Wejście ADC1 (dzielnik ~0.6, zakres ~5 V)"),
        [MpccChannel.Adc2] = new("ADC2", "V", 0.001, "Wejście ADC2 (dzielnik ~0.6, zakres ~5 V)"),
        [MpccChannel.Adc3] = new("ADC3", "V", 0.001, "Wejście ADC3 (dzielnik ~0.6, zakres ~5 V)"),
        [MpccChannel.Adc4] = new("ADC4", "V", 0.001, "Wejście ADC4 (dzielnik ~0.1, zakres ~30 V)"),
        [MpccChannel.Adc5] = new("ADC5", "V", 0.001, "Wejście ADC5 (dzielnik ~0.1, zakres ~30 V)"),
        [MpccChannel.Adc6] = new("ADC6", "V", 0.001, "Wejście ADC6 (dzielnik ~0.1, zakres ~30 V)"),
        [MpccChannel.Vdda] = new("VDDA", "V", 0.001, "Napięcie referencyjne/zasilania analogowego (~3.0 V, LM4040)"),
        [MpccChannel.McuTemp] = new("Temp. MCU", "°C", 0.01, "Temperatura złącza MCU (wewnętrzny czujnik ADC)"),
    };

    public static readonly IReadOnlyDictionary<MpccSensor, string> SensorNames = new Dictionary<MpccSensor, string>
    {
        [MpccSensor.Sts31] = "STS31 (temperatura płytki)",
        [MpccSensor.Pcf8574] = "PCF8574 (LED RGB + ALERT)",
        [MpccSensor.Mcu] = "MCU (ADC wewnętrzny)",
    };

    /// <summary>Bit order of the GET_STATUS PRESENT/FAULT bitmaps — positional, matches firmware's wsc_sensor_ids.</summary>
    public static readonly IReadOnlyList<MpccSensor> SensorBitOrder = [MpccSensor.Sts31, MpccSensor.Pcf8574, MpccSensor.Mcu];

    /// <summary>Which quantities each sensor actually reports via READ_SENSOR — straight from protocol.yaml's <c>sensors:</c> section. Drives CSV column selection (no point in a column that can never have data).</summary>
    public static readonly IReadOnlyDictionary<MpccSensor, IReadOnlyList<MpccQuantity>> SensorProvides = new Dictionary<MpccSensor, IReadOnlyList<MpccQuantity>>
    {
        [MpccSensor.Sts31] = [MpccQuantity.Temp],
        [MpccSensor.Pcf8574] = [],
        [MpccSensor.Mcu] = [MpccQuantity.McuTemp, MpccQuantity.Vdda, MpccQuantity.Vbat],
    };

    public static readonly IReadOnlyDictionary<MpccQuantity, QuantityInfo> Quantities = new Dictionary<MpccQuantity, QuantityInfo>
    {
        [MpccQuantity.Temp] = new("Temperatura", "°C", 0.01),
        [MpccQuantity.McuTemp] = new("Temp. MCU", "°C", 0.01),
        [MpccQuantity.Vdda] = new("VDDA", "V", 0.001),
        [MpccQuantity.Vbat] = new("VBAT", "V", 0.001),
        [MpccQuantity.AdcV] = new("Napięcie AFE", "V", 0.001),
    };
}

/// <summary>Canonical UPPER_SNAKE names (matching protocol.yaml) used as CSV column / live-value-store keys.</summary>
public static class MpccNames
{
    public static string Of(MpccChannel c) => c switch
    {
        MpccChannel.Temp => "TEMP",
        MpccChannel.Adc0 => "ADC0",
        MpccChannel.Adc1 => "ADC1",
        MpccChannel.Adc2 => "ADC2",
        MpccChannel.Adc3 => "ADC3",
        MpccChannel.Adc4 => "ADC4",
        MpccChannel.Adc5 => "ADC5",
        MpccChannel.Adc6 => "ADC6",
        MpccChannel.Vdda => "VDDA",
        MpccChannel.McuTemp => "MCU_TEMP",
        _ => c.ToString(),
    };

    public static string Of(MpccSensor s) => s switch
    {
        MpccSensor.Sts31 => "STS31",
        MpccSensor.Pcf8574 => "PCF8574",
        MpccSensor.Mcu => "MCU",
        _ => s.ToString(),
    };

    public static string Of(MpccQuantity q) => q switch
    {
        MpccQuantity.Temp => "TEMP",
        MpccQuantity.McuTemp => "MCU_TEMP",
        MpccQuantity.Vdda => "VDDA",
        MpccQuantity.Vbat => "VBAT",
        MpccQuantity.AdcV => "ADC_V",
        _ => q.ToString(),
    };
}
