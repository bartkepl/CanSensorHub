using CanSensorHub.Core.Protocol;

namespace CanSensorHub.Modules.Mpswp.Protocol;

/// <summary>
/// Rejestr parametrów konfiguracyjnych stacji pogodowej, odwzorowany z MPSWP/protocol/device.yaml.
///
/// Parametry o identyfikatorach 0x01–0x0F należą do warstwy uniwersalnej i mają to samo znaczenie na
/// każdym węźle; pozostałe są specyficzne dla tego urządzenia. Wartości logiczne prezentowane są jako
/// pola wyboru, pola o nazwanych trybach (IRQ_MODE, AS_AFE_MODE, AS_MIN_LIGH) jako listy rozwijane,
/// a wagi fuzji i współczynniki kalibracji jako pola liczbowe z ograniczeniem zakresu, grupowane
/// według czujnika.
/// </summary>
public static class MpswpParams
{
    private static readonly ParamOption[] IrqModeOptions =
    [
        new(0, "Wyłączona"), new(1, "Przerwanie EXTI"), new(2, "Odpytywanie (polling)"),
    ];

    private static readonly ParamOption[] AfeModeOptions = [new(0, "Indoor (czułość wyższa)"), new(1, "Outdoor (mniej fałszywych alarmów)")];

    private static readonly ParamOption[] MinLightningOptions =
    [
        new(0, "1 wyładowanie"), new(1, "5 wyładowań"), new(2, "9 wyładowań"), new(3, "16 wyładowań"),
    ];

    public static readonly IReadOnlyList<ParamDescriptor> All =
    [
        new() { Id = 0x01, Name = "NODE_ID", Type = ParamValueType.U8, Min = 1, Max = 254, Default = 1, Unit = "-", Group = "System",
            Description = "Adres tego węzła na magistrali CAN (1..254). Domyślnie 0x01.", Control = ParamControlKind.Numeric, DisplayHex = true },
        new() { Id = 0x02, Name = "TELEMETRY_PERIOD", Type = ParamValueType.U16, Min = 100, Max = 60000, Default = 2000, Unit = "ms", Group = "System",
            Description = "Okres cyklicznego rozgłaszania telemetrii (ms).", Control = ParamControlKind.Numeric },
        new() { Id = 0x03, Name = "MEASURE_PERIOD", Type = ParamValueType.U16, Min = 1000, Max = 60000, Default = 1000, Unit = "ms", Group = "System",
            Description = "Okres pełnego cyklu odczytu czujników (ms). Min. 1000 wymuszone: SGP41 zakłada próbkowanie 1 Hz, a filtr IQM wewnętrznego ADC (okno 64 próbek/kanał) potrzebuje >=~1 s żeby okno zdążyło się zapełnić.", Control = ParamControlKind.Numeric },
        new() { Id = 0x04, Name = "CAN_TERMINATION", Type = ParamValueType.U8, Min = 0, Max = 1, Default = 1, Unit = "0/1", Group = "System",
            Description = "Rezystor terminujący 120 Ω. 1 = włączony (węzeł na końcu magistrali).", Control = ParamControlKind.Checkbox },
        new() { Id = 0x05, Name = "CAN_SILENT", Type = ParamValueType.U8, Min = 0, Max = 1, Default = 0, Unit = "0/1", Group = "System",
            Description = "Tryb nasłuchu transceivera (bez ACK/nadawania). 1 = tylko nasłuch.", Control = ParamControlKind.Checkbox },
        new() { Id = 0x06, Name = "TELEMETRY_SYNTH", Type = ParamValueType.U8, Min = 0, Max = 1, Default = 0, Unit = "0/1", Group = "System",
            Description = "Gdy kanał nie ma sprawnego czujnika: 0 = NaN, 1 = wartość syntetyczna (demo).", Control = ParamControlKind.Checkbox },
        new() { Id = 0x07, Name = "LOW_POWER", Type = ParamValueType.U8, Min = 0, Max = 1, Default = 1, Unit = "0/1", Group = "System",
            Description = "Uśpienie rdzenia (WFI) między zdarzeniami.", Control = ParamControlKind.Checkbox },
        new() { Id = 0x08, Name = "IRQ_MODE", Type = ParamValueType.U8, Min = 0, Max = 2, Default = 1, Unit = "-", Group = "System",
            Description = "Obsługa przerwań czujników (PCF8574/STS31).", Control = ParamControlKind.ComboBox, Options = IrqModeOptions },

        new() { Id = 0x09, Name = "CAL_LOCK", Type = ParamValueType.U8, Min = 0, Max = 1, Default = 1, Unit = "0/1", Group = "System",
            Description = "Blokada zapisu parametrów kalibracyjnych. 1 = zablokowane; próba zapisu zwraca ERR_READONLY. Parametry kalibracyjne określają metrologię węzła, więc blokada jest domyślnie włączona, a jej zdjęcie stanowi osobną, świadomą czynność.", Control = ParamControlKind.Checkbox },
        new() { Id = 0x0A, Name = "HEARTBEAT_PERIOD", Type = ParamValueType.U16, Min = 1000, Max = 60000, Default = 10000, Unit = "ms", Group = "System",
            Description = "Okres zdarzenia HEARTBEAT. Host uznaje węzeł za utracony po braku zdarzenia przez trzy okresy.", Control = ParamControlKind.Numeric },

        new() { Id = 0x10, Name = "EN_HTU21D", Type = ParamValueType.U8, Min = 0, Max = 1, Default = 1, Group = "Aktywacja czujników", Description = "Aktywacja HTU21D.", Control = ParamControlKind.Checkbox },
        new() { Id = 0x11, Name = "EN_SHT45", Type = ParamValueType.U8, Min = 0, Max = 1, Default = 1, Group = "Aktywacja czujników", Description = "Aktywacja SHT45.", Control = ParamControlKind.Checkbox },
        new() { Id = 0x12, Name = "EN_MPL3115A2", Type = ParamValueType.U8, Min = 0, Max = 1, Default = 1, Group = "Aktywacja czujników", Description = "Aktywacja MPL3115A2.", Control = ParamControlKind.Checkbox },
        new() { Id = 0x13, Name = "EN_LPS25HB", Type = ParamValueType.U8, Min = 0, Max = 1, Default = 1, Group = "Aktywacja czujników", Description = "Aktywacja LPS25HB.", Control = ParamControlKind.Checkbox },
        new() { Id = 0x14, Name = "EN_BME680", Type = ParamValueType.U8, Min = 0, Max = 1, Default = 1, Group = "Aktywacja czujników", Description = "Aktywacja BME680.", Control = ParamControlKind.Checkbox },
        new() { Id = 0x15, Name = "EN_SGP41", Type = ParamValueType.U8, Min = 0, Max = 1, Default = 1, Group = "Aktywacja czujników", Description = "Aktywacja SGP41.", Control = ParamControlKind.Checkbox },
        new() { Id = 0x16, Name = "EN_STS31_CPU", Type = ParamValueType.U8, Min = 0, Max = 1, Default = 1, Group = "Aktywacja czujników", Description = "Aktywacja STS31 (CPU).", Control = ParamControlKind.Checkbox },
        new() { Id = 0x17, Name = "EN_STS31_SENS", Type = ParamValueType.U8, Min = 0, Max = 1, Default = 1, Group = "Aktywacja czujników", Description = "Aktywacja STS31 (czujniki).", Control = ParamControlKind.Checkbox },
        new() { Id = 0x18, Name = "EN_TMP117", Type = ParamValueType.U8, Min = 0, Max = 1, Default = 1, Group = "Aktywacja czujników", Description = "Aktywacja TMP117.", Control = ParamControlKind.Checkbox },
        new() { Id = 0x19, Name = "EN_AS3935", Type = ParamValueType.U8, Min = 0, Max = 1, Default = 1, Group = "Aktywacja czujników", Description = "Aktywacja AS3935.", Control = ParamControlKind.Checkbox },
        new() { Id = 0x1A, Name = "EN_PCF8574", Type = ParamValueType.U8, Min = 0, Max = 1, Default = 1, Group = "Aktywacja czujników", Description = "Aktywacja PCF8574 (ekspander IRQ).", Control = ParamControlKind.Checkbox },
        new() { Id = 0x1B, Name = "EN_MCU_ADC", Type = ParamValueType.U8, Min = 0, Max = 1, Default = 1, Group = "Aktywacja czujników", Description = "Aktywacja wewnętrznego ADC MCU.", Control = ParamControlKind.Checkbox },

        new() { Id = 0x20, Name = "WT_TEMP_HTU21D", Type = ParamValueType.U16, Min = 0, Max = 65535, Default = 64, Unit = "q8", Group = "Wagi fuzji — temperatura", Description = "Waga temp. HTU21D (Q8: 256 = 1.0).", Control = ParamControlKind.Numeric, DecimalPlaces = 0 },
        new() { Id = 0x21, Name = "WT_TEMP_SHT45", Type = ParamValueType.U16, Min = 0, Max = 65535, Default = 230, Unit = "q8", Group = "Wagi fuzji — temperatura", Description = "Waga temp. SHT45 (wysoka dokładność).", Control = ParamControlKind.Numeric, DecimalPlaces = 0 },
        new() { Id = 0x22, Name = "WT_TEMP_MPL3115A2", Type = ParamValueType.U16, Min = 0, Max = 65535, Default = 32, Unit = "q8", Group = "Wagi fuzji — temperatura", Description = "Waga temp. MPL3115A2.", Control = ParamControlKind.Numeric, DecimalPlaces = 0 },
        new() { Id = 0x23, Name = "WT_TEMP_LPS25HB", Type = ParamValueType.U16, Min = 0, Max = 65535, Default = 32, Unit = "q8", Group = "Wagi fuzji — temperatura", Description = "Waga temp. LPS25HB.", Control = ParamControlKind.Numeric, DecimalPlaces = 0 },
        new() { Id = 0x24, Name = "WT_TEMP_BME680", Type = ParamValueType.U16, Min = 0, Max = 65535, Default = 64, Unit = "q8", Group = "Wagi fuzji — temperatura", Description = "Waga temp. BME680.", Control = ParamControlKind.Numeric, DecimalPlaces = 0 },
        new() { Id = 0x25, Name = "WT_TEMP_STS31_CPU", Type = ParamValueType.U16, Min = 0, Max = 65535, Default = 64, Unit = "q8", Group = "Wagi fuzji — temperatura", Description = "Waga temp. STS31 (CPU).", Control = ParamControlKind.Numeric, DecimalPlaces = 0 },
        new() { Id = 0x26, Name = "WT_TEMP_STS31_SENS", Type = ParamValueType.U16, Min = 0, Max = 65535, Default = 128, Unit = "q8", Group = "Wagi fuzji — temperatura", Description = "Waga temp. STS31 (czujniki).", Control = ParamControlKind.Numeric, DecimalPlaces = 0 },
        new() { Id = 0x27, Name = "WT_TEMP_TMP117", Type = ParamValueType.U16, Min = 0, Max = 65535, Default = 256, Unit = "q8", Group = "Wagi fuzji — temperatura", Description = "Waga temp. TMP117 (najwyższa dokładność).", Control = ParamControlKind.Numeric, DecimalPlaces = 0 },

        new() { Id = 0x30, Name = "WT_HUM_HTU21D", Type = ParamValueType.U16, Min = 0, Max = 65535, Default = 64, Unit = "q8", Group = "Wagi fuzji — wilgotność", Description = "Waga wilg. HTU21D.", Control = ParamControlKind.Numeric, DecimalPlaces = 0 },
        new() { Id = 0x31, Name = "WT_HUM_SHT45", Type = ParamValueType.U16, Min = 0, Max = 65535, Default = 256, Unit = "q8", Group = "Wagi fuzji — wilgotność", Description = "Waga wilg. SHT45.", Control = ParamControlKind.Numeric, DecimalPlaces = 0 },
        new() { Id = 0x32, Name = "WT_HUM_BME680", Type = ParamValueType.U16, Min = 0, Max = 65535, Default = 96, Unit = "q8", Group = "Wagi fuzji — wilgotność", Description = "Waga wilg. BME680.", Control = ParamControlKind.Numeric, DecimalPlaces = 0 },

        new() { Id = 0x40, Name = "WT_PRESS_MPL3115A2", Type = ParamValueType.U16, Min = 0, Max = 65535, Default = 128, Unit = "q8", Group = "Wagi fuzji — ciśnienie", Description = "Waga ciśn. MPL3115A2.", Control = ParamControlKind.Numeric, DecimalPlaces = 0 },
        new() { Id = 0x41, Name = "WT_PRESS_LPS25HB", Type = ParamValueType.U16, Min = 0, Max = 65535, Default = 192, Unit = "q8", Group = "Wagi fuzji — ciśnienie", Description = "Waga ciśn. LPS25HB.", Control = ParamControlKind.Numeric, DecimalPlaces = 0 },
        new() { Id = 0x42, Name = "WT_PRESS_BME680", Type = ParamValueType.U16, Min = 0, Max = 65535, Default = 96, Unit = "q8", Group = "Wagi fuzji — ciśnienie", Description = "Waga ciśn. BME680.", Control = ParamControlKind.Numeric, DecimalPlaces = 0 },

        .. TempCal(0x50, 0x51, 0x52, "TMP117"),
        .. TempCal(0x53, 0x54, 0x55, "SHT45"),
        .. TempCal(0x56, 0x57, 0x58, "STS31_CPU"),
        .. TempCal(0x59, 0x5A, 0x5B, "STS31_SENS"),
        .. TempCal(0x5C, 0x5D, 0x5E, "HTU21D"),
        .. TempCal(0x5F, 0x60, 0x61, "LPS25HB"),
        .. TempCal(0x62, 0x63, 0x64, "MPL3115A2"),
        .. TempCal(0x65, 0x66, 0x67, "BME680"),

        .. PressCal(0x68, 0x69, 0x6A, "MPL3115A2"),
        .. PressCal(0x6B, 0x6C, 0x6D, "LPS25HB"),
        .. PressCal(0x6E, 0x6F, 0x70, "BME680"),

        new() { Id = 0x80, Name = "AS_AFE_MODE", Type = ParamValueType.U8, Min = 0, Max = 1, Default = 0, Group = "AS3935 (wyładowania)",
            Description = "Wzmocnienie AFE detektora wyładowań.", Control = ParamControlKind.ComboBox, Options = AfeModeOptions },
        new() { Id = 0x81, Name = "AS_NF_LEV", Type = ParamValueType.U8, Min = 0, Max = 7, Default = 2, Group = "AS3935 (wyładowania)",
            Description = "Próg szumu tła (0..7). Wyżej = większa odporność na szum, mniejsza czułość.", Control = ParamControlKind.Numeric, DecimalPlaces = 0 },
        new() { Id = 0x82, Name = "AS_WDTH", Type = ParamValueType.U8, Min = 0, Max = 15, Default = 2, Group = "AS3935 (wyładowania)",
            Description = "Próg watchdog (0..15). Wyżej = mniej fałszywych wyzwoleń.", Control = ParamControlKind.Numeric, DecimalPlaces = 0 },
        new() { Id = 0x83, Name = "AS_SREJ", Type = ParamValueType.U8, Min = 0, Max = 15, Default = 2, Group = "AS3935 (wyładowania)",
            Description = "Odrzucanie zakłócaczy impulsowych (0..15).", Control = ParamControlKind.Numeric, DecimalPlaces = 0 },
        new() { Id = 0x84, Name = "AS_MIN_LIGH", Type = ParamValueType.U8, Min = 0, Max = 3, Default = 0, Group = "AS3935 (wyładowania)",
            Description = "Minimalna liczba wyładowań przed zgłoszeniem przerwania.", Control = ParamControlKind.ComboBox, Options = MinLightningOptions },
        new() { Id = 0x85, Name = "AS_MASK_DIST", Type = ParamValueType.U8, Min = 0, Max = 1, Default = 0, Group = "AS3935 (wyładowania)",
            Description = "Maskowanie zakłócaczy — mniej przerwań w środowisku EMI.", Control = ParamControlKind.Checkbox },
        new() { Id = 0x86, Name = "AS_TUN_CAP", Type = ParamValueType.U8, Min = 0, Max = 15, Default = 0, Group = "AS3935 (wyładowania)",
            Description = "Pojemność strojenia anteny (krok 8 pF). Ustawiana automatycznie przez kalibrację.", Control = ParamControlKind.Numeric, DecimalPlaces = 0 },
        new() { Id = 0x87, Name = "AS_LCO_OUT", Type = ParamValueType.U8, Min = 0, Max = 1, Default = 0, Storage = ParamStorage.Ram, Group = "AS3935 (wyładowania)",
            Description = "DIAGNOSTYKA: wystawia LCO/16 na pinie IRQ do pomiaru oscyloskopem; wyłącza detekcję wyładowań. Nietrwałe — pamiętaj wyłączyć po pomiarze.",
            Control = ParamControlKind.Checkbox },
    ];

    private static IEnumerable<ParamDescriptor> TempCal(byte c0, byte c1, byte c2, string sensor) =>
    [
        new() { Id = c0, Name = $"CAL_{sensor}_C0", Type = ParamValueType.F32, Min = -50.0, Max = 50.0, Default = 0.0, Unit = "°C", Group = $"Kalibracja — {sensor}",
            Description = $"{sensor}: c0 (przesunięcie). T_skor = c0 + c1·T + c2·T².", Control = ParamControlKind.Numeric, DecimalPlaces = 3 },
        new() { Id = c1, Name = $"CAL_{sensor}_C1", Type = ParamValueType.F32, Min = 0.5, Max = 1.5, Default = 1.0, Unit = "-", Group = $"Kalibracja — {sensor}",
            Description = $"{sensor}: c1 (wzmocnienie).", Control = ParamControlKind.Numeric, DecimalPlaces = 4 },
        new() { Id = c2, Name = $"CAL_{sensor}_C2", Type = ParamValueType.F32, Min = -0.01, Max = 0.01, Default = 0.0, Unit = "1/°C", Group = $"Kalibracja — {sensor}",
            Description = $"{sensor}: c2 (krzywizna).", Control = ParamControlKind.Numeric, DecimalPlaces = 5 },
    ];

    private static IEnumerable<ParamDescriptor> PressCal(byte c0, byte c1, byte c2, string sensor) =>
    [
        new() { Id = c0, Name = $"CAL_PRESS_{sensor}_C0", Type = ParamValueType.F32, Min = -50000.0, Max = 50000.0, Default = 0.0, Unit = "Pa", Group = $"Kalibracja ciśnienia — {sensor}",
            Description = $"{sensor} ciśnienie: c0 (przesunięcie, Pa). P_skor = c0 + c1·P + c2·P².", Control = ParamControlKind.Numeric, DecimalPlaces = 1 },
        new() { Id = c1, Name = $"CAL_PRESS_{sensor}_C1", Type = ParamValueType.F32, Min = 0.5, Max = 1.5, Default = 1.0, Unit = "-", Group = $"Kalibracja ciśnienia — {sensor}",
            Description = $"{sensor} ciśnienie: c1 (wzmocnienie).", Control = ParamControlKind.Numeric, DecimalPlaces = 4 },
        new() { Id = c2, Name = $"CAL_PRESS_{sensor}_C2", Type = ParamValueType.F32, Min = -0.001, Max = 0.001, Default = 0.0, Unit = "1/Pa", Group = $"Kalibracja ciśnienia — {sensor}",
            Description = $"{sensor} ciśnienie: c2 (krzywizna; zwykle 0).", Control = ParamControlKind.Numeric, DecimalPlaces = 6 },
    ];
}
