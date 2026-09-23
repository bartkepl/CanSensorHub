using CanSensorHub.Core.Protocol;

namespace CanSensorHub.Modules.Mpcc.Protocol;

/// <summary>
/// Ported 1:1 z MPCC/protocol/device.yaml oraz warstwy wspolnej common/protocol/. MPCC is
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
    public const byte FwVersionMinor = 2;
    public const byte HwVersionMajor = 1;
    public const byte HwVersionMinor = 0;
    public const ushort BuildRevision = 4;

    /// <summary>Wersja układu 29-bitowego identyfikatora. Zmieniana wyłącznie przy zmianie znaczenia pól identyfikatora, niezależnie od wersji firmware.</summary>
    public const byte ProtocolVersion = 1;

    /// <summary>DEVICE_TYPE z rejestru typów urządzeń warstwy wspólnej.</summary>
    public const ushort DeviceType = 0x0002;

    /// <summary>Profil protokołu, w którym pracuje ten port.</summary>
    public const byte ProfileVersion = 2;

    /// <summary>
    /// Adres, na którym odpowiada bootloader. Wkompilowany w jego obraz i niezależny od NODE_ID
    /// aplikacji — narzędzie flashujące musi znać oba.
    /// </summary>
    public const byte BootloaderNode = 0x10;
}

/// <summary>Bajt BUILD_FLAGS ramki identyfikacyjnej. Obecny w ramkach 21-bajtowych i dłuższych.</summary>
[Flags]
public enum MpccInfoBuildFlags : byte
{
    None = 0,
    /// <summary>Build Debug (konfiguracja Debug w STM32CubeIDE) — nie powinien trafić na urządzenie produkcyjne.</summary>
    Debug = 1 << 0,
}

/// <summary>
/// Bitmapa zdolności zgłaszana przez węzeł. Odpowiada sekcji <c>capabilities</c> pliku
/// <c>MPCC/protocol/device.yaml</c>.
/// <para>
/// UWAGA: jest to ręcznie utrzymywana kopia. Korzysta z niej symulator przy budowaniu sztucznej
/// ramki identyfikacyjnej, więc rozejście się z <c>device.yaml</c> sprawia, że symulowany węzeł
/// opisuje się inaczej niż rzeczywisty. Dług odnotowany w ADR 0001 i 0004 warstwy wspólnej.
/// </para>
/// </summary>
public static class MpccCapabilities
{
    public const uint Mask =
        (uint)(DeviceCapability.Telemetry
             | DeviceCapability.Sensors
             | DeviceCapability.ParamsNv
             | DeviceCapability.Rtc
             | DeviceCapability.Bootloader
             | DeviceCapability.TimeSync
             | DeviceCapability.ListParams
             | DeviceCapability.ListChannels
             | DeviceCapability.Counters
             | DeviceCapability.SelfTest
             | DeviceCapability.CalLock
             | DeviceCapability.AddrConflictDetect
             | DeviceCapability.StatusExt
             | DeviceCapability.Outputs
             | DeviceCapability.Display);
}

/// <summary>
/// Kanały telemetrii. Identyfikator kanału JEST identyfikatorem wielkości fizycznej z rejestru
/// globalnego, więc ten sam numer oznacza tę samą wielkość na każdym węźle magistrali.
/// <para>
/// W profilu 0 wejścia ADC zajmowały 0x02–0x08, gdzie stacja pogodowa ma wilgotność, ciśnienie
/// i wskaźniki jakości powietrza — generyczny host nie miał jak zdekodować wspólnej magistrali.
/// Od profilu 2 zajmują blok napięciowy 0x20–0x26.
/// </para>
/// </summary>
public enum MpccChannel : byte
{
    Temp = 0x01,
    McuTemp = 0x0A,
    Vdda = 0x0B,
    VoltageCh0 = 0x20,
    VoltageCh1 = 0x21,
    VoltageCh2 = 0x22,
    VoltageCh3 = 0x23,
    VoltageCh4 = 0x24,
    VoltageCh5 = 0x25,
    VoltageCh6 = 0x26,
}

public sealed record ChannelInfo(string Label, string Unit, double Scale, string Description);

public enum MpccSensor : byte
{
    Sts31Cpu = 0x07,
    Pcf8574 = 0x0B,
    Mcu = 0x0C,
    /// <summary>Analogowy front-end: siedem wejść napięciowych przez dzielniki.</summary>
    Afe = 0x10,
}

public enum MpccQuantity : byte
{
    Temp = 0x01,
    McuTemp = 0x0A,
    Vdda = 0x0B,
    Vbat = 0x0C,
    VoltageCh0 = 0x20,
    VoltageCh1 = 0x21,
    VoltageCh2 = 0x22,
    VoltageCh3 = 0x23,
    VoltageCh4 = 0x24,
    VoltageCh5 = 0x25,
    VoltageCh6 = 0x26,
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
    EnterBootloader = 0x0D,

    // Komendy uniwersalne wprowadzone w profilu 2. Numeracja zaczyna się od 0x12, ponieważ
    // wartości 0x0E–0x11 były używane w starszych profilach i zostały trwale wycofane.
    GetCapabilities = 0x12,
    ListParams = 0x13,
    ListChannels = 0x14,
    SetTimeSync = 0x15,
    GetCounters = 0x16,

    // Komendy powszechnej praktyki.
    GetStatusExt = 0x20,
    SelfTest = 0x21,
    ClearCounters = 0x22,

    // Komendy specyficzne dla urządzenia. Bit 7 sygnalizuje hostowi, że do zdekodowania
    // potrzebny jest DEVICE_TYPE. W profilu 0 zajmowały 0x0D–0x10, kolidując z przestrzenią
    // uniwersalną — stąd 0x0D oznacza teraz ENTER_BOOTLOADER w całym ekosystemie.
    GetOutput = 0x80,
    SetOutput = 0x81,
    SetLed = 0x82,
    ReadAdc = 0x83,
}

public enum MpccEventCode : byte
{
    Boot = 0x01,
    /// <summary>Uogólnienie dawnego STS31_ALERT — ten sam numer, szersze znaczenie.</summary>
    SensorAlert = 0x03,
    SensorFault = 0x04,
    SensorBack = 0x05,
    BusRecovered = 0x06,
    Heartbeat = 0x07,
    SensorIrq = 0x08,
    CriticalAlarm = 0x09,
    SensorRange = 0x0C,
    AddrConflict = 0x0D,
    ConfigChanged = 0x0E,

    // Zdarzenia specyficzne. W profilu 0 zajmowały 0x0A i 0x0B, gdzie stacja pogodowa miała
    // CALIB_DONE i SENSOR_RANGE — oba numery zostały trwale wycofane.
    Button = 0x80,
    OutputChanged = 0x81,
}

/// <summary>
/// Bajt READING_STATUS rekordu <c>sensor_reading</c>. MPCC nie zgłasza zdolności
/// <see cref="DeviceCapability.RangeLimits"/> i nie prowadzi limitów, więc jego odczyty mają stale
/// <see cref="Ok"/>; pozostałe wartości są tu po to, by rekord z węzła prowadzącego limity dał się
/// odczytać bez zmiany tego modułu.
/// </summary>
public enum MpccReadingStatus : byte
{
    Ok = 0x00,
    WarnLow = 0x01,
    WarnHigh = 0x02,
    ErrLow = 0x03,
    ErrHigh = 0x04,
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
        [MpccChannel.VoltageCh0] = new("Napięcie CH0", "V", 0.001, "Wejście 0 (dzielnik ~0.6, zakres ~5 V)"),
        [MpccChannel.VoltageCh1] = new("Napięcie CH1", "V", 0.001, "Wejście 1 (dzielnik ~0.6, zakres ~5 V)"),
        [MpccChannel.VoltageCh2] = new("Napięcie CH2", "V", 0.001, "Wejście 2 (dzielnik ~0.6, zakres ~5 V)"),
        [MpccChannel.VoltageCh3] = new("Napięcie CH3", "V", 0.001, "Wejście 3 (dzielnik ~0.6, zakres ~5 V)"),
        [MpccChannel.VoltageCh4] = new("Napięcie CH4", "V", 0.001, "Wejście 4 (dzielnik ~0.1, zakres ~30 V)"),
        [MpccChannel.VoltageCh5] = new("Napięcie CH5", "V", 0.001, "Wejście 5 (dzielnik ~0.1, zakres ~30 V)"),
        [MpccChannel.VoltageCh6] = new("Napięcie CH6", "V", 0.001, "Wejście 6 (dzielnik ~0.1, zakres ~30 V)"),
        [MpccChannel.Vdda] = new("VDDA", "V", 0.001, "Napięcie referencyjne/zasilania analogowego (~3.0 V, LM4040)"),
        [MpccChannel.McuTemp] = new("Temp. MCU", "°C", 0.01, "Temperatura złącza MCU (wewnętrzny czujnik ADC)"),
    };

    public static readonly IReadOnlyDictionary<MpccSensor, string> SensorNames = new Dictionary<MpccSensor, string>
    {
        [MpccSensor.Sts31Cpu] = "STS31 (temperatura płytki)",
        [MpccSensor.Pcf8574] = "PCF8574 (LED RGB + ALERT)",
        [MpccSensor.Mcu] = "MCU (ADC wewnętrzny)",
        [MpccSensor.Afe] = "AFE (7 wejść napięciowych)",
    };

    /// <summary>Bit order of the GET_STATUS PRESENT/FAULT bitmaps — positional, matches firmware's wsc_sensor_ids.</summary>
    public static readonly IReadOnlyList<MpccSensor> SensorBitOrder = [MpccSensor.Sts31Cpu, MpccSensor.Pcf8574, MpccSensor.Mcu, MpccSensor.Afe];

    /// <summary>Which quantities each sensor actually reports via READ_SENSOR — straight from protocol.yaml's <c>sensors:</c> section. Drives CSV column selection (no point in a column that can never have data).</summary>
    public static readonly IReadOnlyDictionary<MpccSensor, IReadOnlyList<MpccQuantity>> SensorProvides = new Dictionary<MpccSensor, IReadOnlyList<MpccQuantity>>
    {
        [MpccSensor.Sts31Cpu] = [MpccQuantity.Temp],
        [MpccSensor.Pcf8574] = [],
        [MpccSensor.Mcu] = [MpccQuantity.McuTemp, MpccQuantity.Vdda, MpccQuantity.Vbat],
        [MpccSensor.Afe] =
        [
            MpccQuantity.VoltageCh0, MpccQuantity.VoltageCh1, MpccQuantity.VoltageCh2,
            MpccQuantity.VoltageCh3, MpccQuantity.VoltageCh4, MpccQuantity.VoltageCh5,
            MpccQuantity.VoltageCh6,
        ],
    };

    public static readonly IReadOnlyDictionary<MpccQuantity, QuantityInfo> Quantities = new Dictionary<MpccQuantity, QuantityInfo>
    {
        [MpccQuantity.Temp] = new("Temperatura", "°C", 0.01),
        [MpccQuantity.McuTemp] = new("Temp. MCU", "°C", 0.01),
        [MpccQuantity.Vdda] = new("VDDA", "V", 0.001),
        [MpccQuantity.Vbat] = new("VBAT", "V", 0.001),
        [MpccQuantity.VoltageCh0] = new("Napięcie CH0", "V", 0.001),
        [MpccQuantity.VoltageCh1] = new("Napięcie CH1", "V", 0.001),
        [MpccQuantity.VoltageCh2] = new("Napięcie CH2", "V", 0.001),
        [MpccQuantity.VoltageCh3] = new("Napięcie CH3", "V", 0.001),
        [MpccQuantity.VoltageCh4] = new("Napięcie CH4", "V", 0.001),
        [MpccQuantity.VoltageCh5] = new("Napięcie CH5", "V", 0.001),
        [MpccQuantity.VoltageCh6] = new("Napięcie CH6", "V", 0.001),
    };
}

/// <summary>Canonical UPPER_SNAKE names (matching protocol.yaml) used as CSV column / live-value-store keys.</summary>
public static class MpccNames
{
    public static string Of(MpccChannel c) => c switch
    {
        MpccChannel.Temp => "TEMP",
        MpccChannel.VoltageCh0 => "VOLTAGE_CH0",
        MpccChannel.VoltageCh1 => "VOLTAGE_CH1",
        MpccChannel.VoltageCh2 => "VOLTAGE_CH2",
        MpccChannel.VoltageCh3 => "VOLTAGE_CH3",
        MpccChannel.VoltageCh4 => "VOLTAGE_CH4",
        MpccChannel.VoltageCh5 => "VOLTAGE_CH5",
        MpccChannel.VoltageCh6 => "VOLTAGE_CH6",
        MpccChannel.Vdda => "VDDA",
        MpccChannel.McuTemp => "MCU_TEMP",
        _ => c.ToString(),
    };

    public static string Of(MpccSensor s) => s switch
    {
        MpccSensor.Sts31Cpu => "STS31_CPU",
        MpccSensor.Pcf8574 => "PCF8574",
        MpccSensor.Mcu => "MCU",
        MpccSensor.Afe => "AFE",
        _ => s.ToString(),
    };

    public static string Of(MpccReadingStatus s) => s switch
    {
        MpccReadingStatus.Ok => "OK",
        MpccReadingStatus.WarnLow => "WARN_LOW",
        MpccReadingStatus.WarnHigh => "WARN_HIGH",
        MpccReadingStatus.ErrLow => "ERR_LOW",
        MpccReadingStatus.ErrHigh => "ERR_HIGH",
        _ => s.ToString(),
    };

    public static string Of(MpccQuantity q) => q switch
    {
        MpccQuantity.Temp => "TEMP",
        MpccQuantity.McuTemp => "MCU_TEMP",
        MpccQuantity.Vdda => "VDDA",
        MpccQuantity.Vbat => "VBAT",
        MpccQuantity.VoltageCh0 => "VOLTAGE_CH0",
        MpccQuantity.VoltageCh1 => "VOLTAGE_CH1",
        MpccQuantity.VoltageCh2 => "VOLTAGE_CH2",
        MpccQuantity.VoltageCh3 => "VOLTAGE_CH3",
        MpccQuantity.VoltageCh4 => "VOLTAGE_CH4",
        MpccQuantity.VoltageCh5 => "VOLTAGE_CH5",
        MpccQuantity.VoltageCh6 => "VOLTAGE_CH6",
        _ => q.ToString(),
    };
}
