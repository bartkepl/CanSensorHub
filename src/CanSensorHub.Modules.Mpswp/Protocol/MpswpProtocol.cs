using CanSensorHub.Core.Protocol;

namespace CanSensorHub.Modules.Mpswp.Protocol;

/// <summary>
/// Warstwa urządzenia dla stacji pogodowej, odwzorowana z MPSWP/protocol/device.yaml (profil 2).
///
/// Warstwa uniwersalna — układ 29-bitowego identyfikatora, wartości FUNC, kody statusu, ramkowanie
/// STREAM i GET_STATUS — znajduje się w CanSensorHub.Core.Protocol i jest wspólna dla wszystkich
/// węzłów. Tutaj opisane są wyłącznie elementy specyficzne: kanały telemetrii, zestaw czujników
/// z ważoną fuzją temperatury, wilgotności i ciśnienia, detektor wyładowań AS3935 oraz rejestr
/// parametrów.
/// </summary>
public static class MpswpNodes
{
    public const byte Broadcast = 0xFF;
    public const byte Unconfigured = 0x00;
    public const byte Default = 0x01;
}

/// <summary>
/// Generacja protokołu, względem której zweryfikowano ten port — odwzorowanie sekcji <c>info:</c>
/// z MPSWP/protocol/device.yaml, czyli dokładnie tych liczb, z których firmware buduje odpowiedź
/// GET_INFO.
///
/// Nie jest to "najniższa obsługiwana wersja", lecz zapis stanu: w chwili pisania tego kodu
/// specyfikacja urządzenia podawała poniższe wartości. Każda zmiana protokołu MPSWP wymaga
/// podniesienia tych stałych oraz ponownego sprawdzenia tablic opcode'ów, parametrów i limitów.
///
/// Pominięcie tego kroku jest błędem, który ta klasa ma wykrywać: urządzenie zgłasza wtedy przez
/// GET_INFO nowszą wersję firmware niż zapisana tutaj, co oznacza, że kształt którejś komendy na
/// magistrali mógł ulec zmianie bez odpowiadającej zmiany interpretacji po stronie hosta.
/// MpswpDeviceViewModel porównuje obie wartości przy każdym GET_INFO i zgłasza niezgodność.
/// </summary>
public static class MpswpInfo
{
    public const byte FwVersionMajor = 3;
    public const byte FwVersionMinor = 0;
    public const byte HwVersionMajor = 1;
    public const byte HwVersionMinor = 2;
    public const ushort BuildRevision = 8;

    /// <summary>Wersja układu 29-bitowego identyfikatora. Zmieniana wyłącznie przy zmianie znaczenia pól identyfikatora, niezależnie od wersji firmware.</summary>
    public const byte ProtocolVersion = 1;

    /// <summary>DEVICE_TYPE z rejestru typów urządzeń warstwy wspólnej.</summary>
    public const ushort DeviceType = 0x0001;

    /// <summary>Profil protokołu, w którym pracuje ten port.</summary>
    public const byte ProfileVersion = 2;

    /// <summary>
    /// Adres, na którym odpowiada bootloader. Wkompilowany w jego obraz i niezależny od NODE_ID
    /// aplikacji — narzędzie flashujące musi znać oba.
    /// </summary>
    public const byte BootloaderNode = 0x01;
}

/// <summary>Bajt BUILD_FLAGS ramki identyfikacyjnej. Obecny w ramkach 21-bajtowych i dłuższych.</summary>
[Flags]
public enum MpswpInfoBuildFlags : byte
{
    None = 0,
    /// <summary>Build Debug (STM32CubeIDE Debug configuration) — should never appear on a production device.</summary>
    Debug = 1 << 0,
}

public enum MpswpChannel : byte
{
    Temp = 0x01,
    Hum = 0x02,
    Press = 0x03,
    VocIndex = 0x04,
    NoxIndex = 0x05,
    Iaq = 0x06,
}

public sealed record ChannelInfo(string Label, string Unit, double Scale, string Description);

public enum MpswpSensor : byte
{
    Htu21D = 0x01,
    Sht45 = 0x02,
    Mpl3115A2 = 0x03,
    Lps25Hb = 0x04,
    Bme680 = 0x05,
    Sgp41 = 0x06,
    Sts31Cpu = 0x07,
    Sts31Sens = 0x08,
    Tmp117 = 0x09,
    As3935 = 0x0A,
    Pcf8574 = 0x0B,
    Mcu = 0x0C,
}

public enum MpswpQuantity : byte
{
    Temp = 0x01,
    Hum = 0x02,
    Press = 0x03,
    VocIndex = 0x04,
    NoxIndex = 0x05,
    Iaq = 0x06,
    GasRes = 0x07,
    VocRaw = 0x08,
    NoxRaw = 0x09,
    McuTemp = 0x0A,
    Vdda = 0x0B,
    Vbat = 0x0C,
    LightningDist = 0x0D,
    LightningEnergy = 0x0E,
}

public sealed record QuantityInfo(string Label, string Unit, double Scale);

public enum MpswpReqOp : byte
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

    /// <summary>Autokalibracja anteny AS3935. W profilu 1 komenda miała numer 0x0E, kolidujący z przestrzenią uniwersalną.</summary>
    CalibAntenna = 0x80,
}

public enum MpswpEventCode : byte
{
    Boot = 0x01,

    /// <summary>Czujnik zgłosił alarm progowy linią ALERT. W profilu 1 zdarzenie nosiło nazwę STS31_ALERT; numer pozostaje ten sam, znaczenie zostało uogólnione.</summary>
    SensorAlert = 0x03,

    SensorFault = 0x04,
    SensorBack = 0x05,
    BusRecovered = 0x06,
    Heartbeat = 0x07,
    SensorIrq = 0x08,
    CriticalAlarm = 0x09,

    /// <summary>Zmiana statusu odczytu względem limitów. W profilu 1 zdarzenie miało numer 0x0B, używany przez MPCC dla OUTPUT_CHANGED.</summary>
    SensorRange = 0x0C,

    AddrConflict = 0x0D,
    ConfigChanged = 0x0E,

    /// <summary>Wykrycie wyładowania atmosferycznego. W profilu 1 numer 0x02.</summary>
    Lightning = 0x80,

    /// <summary>Zakończenie autokalibracji anteny. W profilu 1 numer 0x0A, używany przez MPCC dla BUTTON.</summary>
    CalibDone = 0x81,
}

/// <summary>
/// Status of one (sensor, quantity) reading against its limits — jak HART cmd 48. WARN_* still counts as
/// valid (takes part in fusion); ERR_* means the reading was excluded from fusion but is still reported
/// diagnostically via READ_SENSOR. Authoritative classification always happens on the device; this enum
/// only decodes what the device already reported (READ_SENSOR status byte, EVENT SENSOR_RANGE payload).
/// </summary>
public enum MpswpReadingStatus : byte
{
    Ok = 0x00,
    WarnLow = 0x01,
    WarnHigh = 0x02,
    ErrLow = 0x03,
    ErrHigh = 0x04,
}

public static class MpswpEventSource
{
    public const byte System = 0x00;
}

public static class MpswpTables
{
    public static readonly IReadOnlyDictionary<MpswpChannel, ChannelInfo> Channels = new Dictionary<MpswpChannel, ChannelInfo>
    {
        [MpswpChannel.Temp] = new("Temperatura", "°C", 0.01, "Temperatura (średnia ważona)"),
        [MpswpChannel.Hum] = new("Wilgotność", "%RH", 0.01, "Wilgotność względna (średnia ważona)"),
        [MpswpChannel.Press] = new("Ciśnienie", "Pa", 1.0, "Ciśnienie (średnia ważona)"),
        [MpswpChannel.VocIndex] = new("Indeks VOC", "idx", 1.0, "Indeks VOC (SGP41, 1..500)"),
        [MpswpChannel.NoxIndex] = new("Indeks NOx", "idx", 1.0, "Indeks NOx (SGP41, 1..500)"),
        [MpswpChannel.Iaq] = new("IAQ (BME680)", "idx", 1.0, "IAQ z BME680/BSEC (opcjonalne)"),
    };

    public static readonly IReadOnlyDictionary<MpswpSensor, string> SensorNames = new Dictionary<MpswpSensor, string>
    {
        [MpswpSensor.Htu21D] = "HTU21D",
        [MpswpSensor.Sht45] = "SHT45",
        [MpswpSensor.Mpl3115A2] = "MPL3115A2",
        [MpswpSensor.Lps25Hb] = "LPS25HB",
        [MpswpSensor.Bme680] = "BME680",
        [MpswpSensor.Sgp41] = "SGP41 (VOC/NOx)",
        [MpswpSensor.Sts31Cpu] = "STS31 (CPU)",
        [MpswpSensor.Sts31Sens] = "STS31 (czujniki)",
        [MpswpSensor.Tmp117] = "TMP117",
        [MpswpSensor.As3935] = "AS3935 (wyładowania)",
        [MpswpSensor.Pcf8574] = "PCF8574 (ekspander IRQ)",
        [MpswpSensor.Mcu] = "MCU (ADC wewnętrzny)",
    };

    /// <summary>Bit order of the GET_STATUS PRESENT/FAULT bitmaps — positional, matches firmware's wsc_sensor_ids.</summary>
    public static readonly IReadOnlyList<MpswpSensor> SensorBitOrder =
    [
        MpswpSensor.Htu21D, MpswpSensor.Sht45, MpswpSensor.Mpl3115A2, MpswpSensor.Lps25Hb, MpswpSensor.Bme680,
        MpswpSensor.Sgp41, MpswpSensor.Sts31Cpu, MpswpSensor.Sts31Sens, MpswpSensor.Tmp117, MpswpSensor.As3935,
        MpswpSensor.Pcf8574, MpswpSensor.Mcu,
    ];

    /// <summary>Which quantities each sensor actually reports via READ_SENSOR — straight from protocol.yaml's <c>sensors:</c> section. Drives CSV column selection (no point in a column that can never have data).</summary>
    public static readonly IReadOnlyDictionary<MpswpSensor, IReadOnlyList<MpswpQuantity>> SensorProvides = new Dictionary<MpswpSensor, IReadOnlyList<MpswpQuantity>>
    {
        [MpswpSensor.Htu21D] = [MpswpQuantity.Temp, MpswpQuantity.Hum],
        [MpswpSensor.Sht45] = [MpswpQuantity.Temp, MpswpQuantity.Hum],
        [MpswpSensor.Mpl3115A2] = [MpswpQuantity.Temp, MpswpQuantity.Press],
        [MpswpSensor.Lps25Hb] = [MpswpQuantity.Temp, MpswpQuantity.Press],
        [MpswpSensor.Bme680] = [MpswpQuantity.Temp, MpswpQuantity.Hum, MpswpQuantity.Press, MpswpQuantity.GasRes],
        [MpswpSensor.Sgp41] = [MpswpQuantity.VocRaw, MpswpQuantity.NoxRaw],
        [MpswpSensor.Sts31Cpu] = [MpswpQuantity.Temp],
        [MpswpSensor.Sts31Sens] = [MpswpQuantity.Temp],
        [MpswpSensor.Tmp117] = [MpswpQuantity.Temp],
        [MpswpSensor.As3935] = [MpswpQuantity.LightningDist],
        [MpswpSensor.Pcf8574] = [],
        [MpswpSensor.Mcu] = [MpswpQuantity.McuTemp, MpswpQuantity.Vdda, MpswpQuantity.Vbat],
    };

    public static readonly IReadOnlyDictionary<MpswpQuantity, QuantityInfo> Quantities = new Dictionary<MpswpQuantity, QuantityInfo>
    {
        [MpswpQuantity.Temp] = new("Temperatura", "°C", 0.01),
        [MpswpQuantity.Hum] = new("Wilgotność", "%RH", 0.01),
        [MpswpQuantity.Press] = new("Ciśnienie", "Pa", 1.0),
        [MpswpQuantity.GasRes] = new("Rezystancja gazu", "Ω", 1.0),
        [MpswpQuantity.VocRaw] = new("VOC (surowe)", "raw", 1.0),
        [MpswpQuantity.NoxRaw] = new("NOx (surowe)", "raw", 1.0),
        [MpswpQuantity.VocIndex] = new("Indeks VOC", "idx", 1.0),
        [MpswpQuantity.NoxIndex] = new("Indeks NOx", "idx", 1.0),
        [MpswpQuantity.LightningDist] = new("Dystans wyładowania", "km", 1.0),
        [MpswpQuantity.McuTemp] = new("Temp. MCU", "°C", 0.01),
        [MpswpQuantity.Vdda] = new("VDDA", "V", 0.001),
        [MpswpQuantity.Vbat] = new("VBAT", "V", 0.001),
    };
}

/// <summary>Canonical UPPER_SNAKE names (matching protocol.yaml) used as CSV column / live-value-store keys.</summary>
public static class MpswpNames
{
    public static string Of(MpswpChannel c) => c switch
    {
        MpswpChannel.Temp => "TEMP",
        MpswpChannel.Hum => "HUM",
        MpswpChannel.Press => "PRESS",
        MpswpChannel.VocIndex => "VOC_INDEX",
        MpswpChannel.NoxIndex => "NOX_INDEX",
        // Nazwa kolumny zachowana z profilu 1, mimo że kanał nazywa się teraz IAQ. Identyfikatory
        // kanałów pozostawiono niezmienione właśnie dla ciągłości danych historycznych; zmiana
        // nagłówka kolumny zerwałaby tę ciągłość w narzędziach czytających dotychczasowe pliki CSV.
        MpswpChannel.Iaq => "IAQ_BME",
        _ => c.ToString(),
    };

    public static string Of(MpswpSensor s) => s switch
    {
        MpswpSensor.Htu21D => "HTU21D",
        MpswpSensor.Sht45 => "SHT45",
        MpswpSensor.Mpl3115A2 => "MPL3115A2",
        MpswpSensor.Lps25Hb => "LPS25HB",
        MpswpSensor.Bme680 => "BME680",
        MpswpSensor.Sgp41 => "SGP41",
        MpswpSensor.Sts31Cpu => "STS31_CPU",
        MpswpSensor.Sts31Sens => "STS31_SENS",
        MpswpSensor.Tmp117 => "TMP117",
        MpswpSensor.As3935 => "AS3935",
        MpswpSensor.Pcf8574 => "PCF8574",
        MpswpSensor.Mcu => "MCU",
        _ => s.ToString(),
    };

    public static string Of(MpswpQuantity q) => q switch
    {
        MpswpQuantity.Temp => "TEMP",
        MpswpQuantity.Hum => "HUM",
        MpswpQuantity.Press => "PRESS",
        MpswpQuantity.GasRes => "GAS_RES",
        MpswpQuantity.VocRaw => "VOC_RAW",
        MpswpQuantity.NoxRaw => "NOX_RAW",
        MpswpQuantity.VocIndex => "VOC_INDEX",
        MpswpQuantity.NoxIndex => "NOX_INDEX",
        MpswpQuantity.LightningDist => "LIGHTNING",
        MpswpQuantity.McuTemp => "MCU_TEMP",
        MpswpQuantity.Vdda => "VDDA",
        MpswpQuantity.Vbat => "VBAT",
        _ => q.ToString(),
    };

    public static string Of(MpswpReadingStatus s) => s switch
    {
        MpswpReadingStatus.Ok => "OK",
        MpswpReadingStatus.WarnLow => "WARN_LOW",
        MpswpReadingStatus.WarnHigh => "WARN_HIGH",
        MpswpReadingStatus.ErrLow => "ERR_LOW",
        MpswpReadingStatus.ErrHigh => "ERR_HIGH",
        _ => s.ToString(),
    };
}

/// <summary>
/// LPL/UPL (process — physically plausible on Earth, shared by every sensor of a quantity) and LTL/UTL
/// (transducer — what a given sensor can actually measure per its datasheet) limits, ported 1:1 from
/// protocol.yaml's <c>quantity_process_limits</c> / <c>transducer_limits</c> (jak HART cmd 48). These are
/// compile-time constants baked into the firmware — not readable/writable as CAN parameters — so this
/// table exists here purely for local classification (the simulator) and reference display; the
/// authoritative status for a real device always comes over the bus (READ_SENSOR status byte, telemetry
/// RANGE_WARN/RANGE_ERR_DROPPED flags, EVENT SENSOR_RANGE).
/// </summary>
public static class MpswpLimits
{
    /// <summary>Zakres podstawowy (LPL/UPL), fizyczne jednostki jak w <see cref="MpswpTables.Quantities"/>. Tylko TEMP/HUM/PRESS mają sensowny "rekord świata".</summary>
    public static readonly IReadOnlyDictionary<MpswpQuantity, (double Lpl, double Upl)> ProcessLimits = new Dictionary<MpswpQuantity, (double, double)>
    {
        [MpswpQuantity.Temp] = (-90.0, 60.0),
        [MpswpQuantity.Hum] = (0.0, 100.0),
        [MpswpQuantity.Press] = (87000.0, 109000.0),
    };

    /// <summary>Zakres transduktora (LTL/UTL) per para (czujnik, wielkość), fizyczne jednostki. WARTOŚCI DO WERYFIKACJI wobec faktycznych kart katalogowych przed produkcyjnym użyciem (patrz protocol.yaml).</summary>
    public static readonly IReadOnlyDictionary<(MpswpSensor Sensor, MpswpQuantity Qty), (double Ltl, double Utl)> TransducerLimits =
        new Dictionary<(MpswpSensor, MpswpQuantity), (double, double)>
        {
            [(MpswpSensor.Htu21D, MpswpQuantity.Temp)] = (-40.0, 125.0),
            [(MpswpSensor.Htu21D, MpswpQuantity.Hum)] = (0.0, 100.0),
            [(MpswpSensor.Sht45, MpswpQuantity.Temp)] = (-40.0, 125.0),
            [(MpswpSensor.Sht45, MpswpQuantity.Hum)] = (0.0, 100.0),
            [(MpswpSensor.Mpl3115A2, MpswpQuantity.Temp)] = (-40.0, 85.0),
            [(MpswpSensor.Mpl3115A2, MpswpQuantity.Press)] = (20000.0, 110000.0),
            [(MpswpSensor.Lps25Hb, MpswpQuantity.Temp)] = (-40.0, 85.0),
            [(MpswpSensor.Lps25Hb, MpswpQuantity.Press)] = (26000.0, 126000.0),
            [(MpswpSensor.Bme680, MpswpQuantity.Temp)] = (-40.0, 85.0),
            [(MpswpSensor.Bme680, MpswpQuantity.Hum)] = (0.0, 100.0),
            [(MpswpSensor.Bme680, MpswpQuantity.Press)] = (30000.0, 110000.0),
            [(MpswpSensor.Sts31Cpu, MpswpQuantity.Temp)] = (-40.0, 125.0),
            [(MpswpSensor.Sts31Sens, MpswpQuantity.Temp)] = (-40.0, 125.0),
            [(MpswpSensor.Tmp117, MpswpQuantity.Temp)] = (-55.0, 150.0),
            [(MpswpSensor.As3935, MpswpQuantity.LightningDist)] = (1.0, 40.0),
            [(MpswpSensor.Mcu, MpswpQuantity.McuTemp)] = (-40.0, 125.0),
            [(MpswpSensor.Mcu, MpswpQuantity.Vdda)] = (1.71, 3.6),
            [(MpswpSensor.Mcu, MpswpQuantity.Vbat)] = (1.6, 3.6),
        };

    /// <summary>Mirrors firmware's wsc_limits_check(): LTL/UTL (transducer) is checked first and takes priority — a sensor that physically cannot measure this is a harder fault than merely an implausible reading.</summary>
    public static MpswpReadingStatus Check(MpswpSensor sensor, MpswpQuantity qty, double physicalValue)
    {
        if (TransducerLimits.TryGetValue((sensor, qty), out var t))
        {
            if (physicalValue < t.Ltl) return MpswpReadingStatus.ErrLow;
            if (physicalValue > t.Utl) return MpswpReadingStatus.ErrHigh;
        }
        if (ProcessLimits.TryGetValue(qty, out var p))
        {
            if (physicalValue < p.Lpl) return MpswpReadingStatus.WarnLow;
            if (physicalValue > p.Upl) return MpswpReadingStatus.WarnHigh;
        }
        return MpswpReadingStatus.Ok;
    }

    public static string Describe(MpswpReadingStatus status) => status switch
    {
        MpswpReadingStatus.Ok => "OK",
        MpswpReadingStatus.WarnLow => "poniżej LPL (zakres podstawowy)",
        MpswpReadingStatus.WarnHigh => "powyżej UPL (zakres podstawowy)",
        MpswpReadingStatus.ErrLow => "poniżej LTL (zakres transduktora) — odrzucone",
        MpswpReadingStatus.ErrHigh => "powyżej UTL (zakres transduktora) — odrzucone",
        _ => status.ToString(),
    };

    /// <summary>The limit pair a non-OK status was actually classified against — LTL/UTL for ERR_* (the transducer range that rejected it), LPL/UPL for WARN_* (the process range it fell outside of). Lets the UI show "why" next to a flagged reading.</summary>
    public static bool TryGetRelevantRange(MpswpSensor sensor, MpswpQuantity qty, MpswpReadingStatus status, out double lo, out double hi)
    {
        switch (status)
        {
            case MpswpReadingStatus.ErrLow or MpswpReadingStatus.ErrHigh when TransducerLimits.TryGetValue((sensor, qty), out var t):
                (lo, hi) = t;
                return true;
            case MpswpReadingStatus.WarnLow or MpswpReadingStatus.WarnHigh when ProcessLimits.TryGetValue(qty, out var p):
                (lo, hi) = p;
                return true;
            default:
                (lo, hi) = (0, 0);
                return false;
        }
    }
}

/// <summary>
/// Zdolności zgłaszane przez stację pogodową w ramce identyfikacyjnej i przez komendę
/// GET_CAPABILITIES. Wartość odpowiada polu <c>capabilities</c> w MPSWP/protocol/device.yaml.
///
/// Host generyczny wykorzystuje tę bitmapę do dostosowania interfejsu bez wbudowanej wiedzy o typie
/// urządzenia; moduł MPSWP używa jej do sprawdzenia, czy podłączony węzeł rzeczywiście udostępnia
/// funkcje, na których opiera się jego widok.
/// </summary>
public static class MpswpCapabilities
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
             | DeviceCapability.RangeLimits
             | DeviceCapability.AddrConflictDetect
             | DeviceCapability.StatusExt
             | DeviceCapability.SensorFusion
             | DeviceCapability.Lightning);
}
