using CanSensorHub.Core.Protocol;

namespace CanSensorHub.Modules.Mpcc.Protocol;

/// <summary>
/// MPCC configuration parameter registry (READ_PARAM/WRITE_PARAM, ARG=Id) — ported from protocol.yaml
/// with a WPF control kind chosen per parameter: booleans as checkboxes, small named-mode fields as combo
/// boxes, everything else (addresses, periods, calibration coefficients) as bounded numeric fields.
/// </summary>
public static class MpccParams
{
    private static readonly ParamOption[] OffAutoManual =
    [
        new(0, "Wyłączona"), new(1, "Automatyczna"), new(2, "Ręczna"),
    ];

    private static readonly ParamOption[] IrqModeOptions =
    [
        new(0, "Wyłączona"), new(1, "Przerwanie EXTI"), new(2, "Odpytywanie (polling)"),
    ];

    public static readonly IReadOnlyList<ParamDescriptor> All =
    [
        new() { Id = 0x01, Name = "NODE_ID", Type = ParamValueType.U8, Min = 1, Max = 254, Default = 16, Unit = "-", Group = "System",
            Description = "Adres tego węzła na magistrali CAN (1..254). Domyślnie 0x10 (16), aby nie kolidować z MPSWP.",
            Control = ParamControlKind.Numeric, DisplayHex = true },
        new() { Id = 0x02, Name = "TELEMETRY_PERIOD", Type = ParamValueType.U16, Min = 100, Max = 60000, Default = 2000, Unit = "ms", Group = "System",
            Description = "Okres cyklicznego rozgłaszania telemetrii (ms).", Control = ParamControlKind.Numeric },
        new() { Id = 0x03, Name = "MEASURE_PERIOD", Type = ParamValueType.U16, Min = 100, Max = 60000, Default = 1000, Unit = "ms", Group = "System",
            Description = "Okres pełnego cyklu odczytu (STS31 + ADC) w ms.", Control = ParamControlKind.Numeric },
        new() { Id = 0x04, Name = "CAN_TERMINATION", Type = ParamValueType.U8, Min = 0, Max = 1, Default = 1, Unit = "0/1", Group = "System",
            Description = "Rezystor terminujący 120 Ω (TS5A21366). 1 = włączony.", Control = ParamControlKind.Checkbox },
        new() { Id = 0x05, Name = "CAN_SILENT", Type = ParamValueType.U8, Min = 0, Max = 1, Default = 0, Unit = "0/1", Group = "System",
            Description = "Tryb nasłuchu transceivera TJA1051 (bez ACK/nadawania). 1 = tylko nasłuch.", Control = ParamControlKind.Checkbox },
        new() { Id = 0x06, Name = "TELEMETRY_SYNTH", Type = ParamValueType.U8, Min = 0, Max = 1, Default = 0, Unit = "0/1", Group = "System",
            Description = "Gdy kanał nie ma danych: 0 = NaN (VALID=0), 1 = wartość syntetyczna (demo).", Control = ParamControlKind.Checkbox },
        new() { Id = 0x07, Name = "LOW_POWER", Type = ParamValueType.U8, Min = 0, Max = 1, Default = 1, Unit = "0/1", Group = "System",
            Description = "Uśpienie rdzenia (WFI) między zdarzeniami. Nie wpływa na timing.", Control = ParamControlKind.Checkbox },
        new() { Id = 0x08, Name = "IRQ_MODE", Type = ParamValueType.U8, Min = 0, Max = 2, Default = 1, Unit = "-", Group = "System",
            Description = "Obsługa przerwań PCF8574.", Control = ParamControlKind.ComboBox, Options = IrqModeOptions },
        new() { Id = 0x09, Name = "CAL_LOCK", Type = ParamValueType.U8, Min = 0, Max = 1, Default = 1, Unit = "0/1", Group = "System",
            Description = "Blokada zapisu parametrów kalibracyjnych. 1 = zablokowane (zapis CAL_* odrzucany statusem ERR_READONLY).",
            Control = ParamControlKind.Checkbox },

        new() { Id = 0x10, Name = "EN_STS31", Type = ParamValueType.U8, Min = 0, Max = 1, Default = 1, Unit = "-", Group = "Aktywacja czujników",
            Description = "Aktywacja czujnika temperatury STS31-DIS.", Control = ParamControlKind.Checkbox },
        new() { Id = 0x11, Name = "EN_PCF8574", Type = ParamValueType.U8, Min = 0, Max = 1, Default = 1, Unit = "-", Group = "Aktywacja czujników",
            Description = "Aktywacja ekspandera PCF8574 (LED RGB + wejście ALERT).", Control = ParamControlKind.Checkbox },
        new() { Id = 0x12, Name = "EN_MCU_ADC", Type = ParamValueType.U8, Min = 0, Max = 1, Default = 1, Unit = "-", Group = "Aktywacja czujników",
            Description = "Aktywacja wewnętrznego ADC MCU (temp. złącza, VDDA, VBAT).", Control = ParamControlKind.Checkbox },
        new() { Id = 0x13, Name = "EN_AFE", Type = ParamValueType.U8, Min = 0, Max = 1, Default = 1, Unit = "-", Group = "Aktywacja czujników",
            Description = "Aktywacja pomiaru 7 zewnętrznych kanałów ADC (dzielniki).", Control = ParamControlKind.Checkbox },

        new() { Id = 0x14, Name = "OD_DEFAULT", Type = ParamValueType.U8, Min = 0, Max = 1, Default = 0, Unit = "0/1", Group = "Wyjście i sygnalizacja",
            Description = "Stan wyjścia open-drain (PB0) po starcie. 0 = wyłączone.", Control = ParamControlKind.Checkbox },
        new() { Id = 0x15, Name = "LED_MODE", Type = ParamValueType.U8, Min = 0, Max = 2, Default = 1, Unit = "-", Group = "Wyjście i sygnalizacja",
            Description = "Sygnalizacja LED RGB.", Control = ParamControlKind.ComboBox, Options = OffAutoManual },
        new() { Id = 0x16, Name = "DISPLAY_MODE", Type = ParamValueType.U8, Min = 0, Max = 1, Default = 1, Unit = "0/1", Group = "Wyjście i sygnalizacja",
            Description = "OLED włączony (menu aktywne).", Control = ParamControlKind.Checkbox },
        new() { Id = 0x17, Name = "DISPLAY_FLIP", Type = ParamValueType.U8, Min = 0, Max = 1, Default = 1, Unit = "0/1", Group = "Wyjście i sygnalizacja",
            Description = "Obrót ekranu OLED o 180°. Domyślnie 1 — panel zamontowany \"do góry nogami\".", Control = ParamControlKind.Checkbox },
        new() { Id = 0x18, Name = "DISPLAY_HOME_S", Type = ParamValueType.U16, Min = 0, Max = 3600, Default = 15, Unit = "s", Group = "Wyjście i sygnalizacja",
            Description = "Po tylu sekundach bezczynności przycisku OLED wraca do ekranu głównego. 0 = wyłączone.", Control = ParamControlKind.Numeric },
        new() { Id = 0x19, Name = "DISPLAY_SLEEP_S", Type = ParamValueType.U16, Min = 0, Max = 3600, Default = 20, Unit = "s", Group = "Wyjście i sygnalizacja",
            Description = "Po tylu sekundach bezczynności OLED gaśnie (oszczędzanie prądu). 0 = nigdy.", Control = ParamControlKind.Numeric },

        .. CalTriplet(0x30, 0x40, 0x50, "ADC0", "0..~5 V", 1.6667),
        .. CalTriplet(0x31, 0x41, 0x51, "ADC1", "0..~5 V", 1.6667),
        .. CalTriplet(0x32, 0x42, 0x52, "ADC2", "0..~5 V", 1.6667),
        .. CalTriplet(0x33, 0x43, 0x53, "ADC3", "0..~5 V", 1.6667),
        .. CalTriplet(0x34, 0x44, 0x54, "ADC4", "0..~30 V", 10.0),
        .. CalTriplet(0x35, 0x45, 0x55, "ADC5", "0..~30 V", 10.0),
        .. CalTriplet(0x36, 0x46, 0x56, "ADC6", "0..~30 V", 10.0),

        new() { Id = 0x57, Name = "CAL_ADC_TREF", Type = ParamValueType.F32, Min = -40.0, Max = 125.0, Default = 25.0, Unit = "°C", Group = "Kalibracja ADC", Calibration = true,
            Description = "Temperatura odniesienia Tref kompensacji ADC (z STS31).", Control = ParamControlKind.Numeric, DecimalPlaces = 1 },

        new() { Id = 0x58, Name = "CAL_STS31_C0", Type = ParamValueType.F32, Min = -50.0, Max = 50.0, Default = 0.0, Unit = "°C", Group = "Kalibracja STS31", Calibration = true,
            Description = "STS31: c0 (przesunięcie). T_skor = c0 + c1·T + c2·T².", Control = ParamControlKind.Numeric, DecimalPlaces = 3 },
        new() { Id = 0x59, Name = "CAL_STS31_C1", Type = ParamValueType.F32, Min = 0.5, Max = 1.5, Default = 1.0, Unit = "-", Group = "Kalibracja STS31", Calibration = true,
            Description = "STS31: c1 (wzmocnienie).", Control = ParamControlKind.Numeric, DecimalPlaces = 4 },
        new() { Id = 0x5A, Name = "CAL_STS31_C2", Type = ParamValueType.F32, Min = -0.01, Max = 0.01, Default = 0.0, Unit = "1/°C", Group = "Kalibracja STS31", Calibration = true,
            Description = "STS31: c2 (krzywizna).", Control = ParamControlKind.Numeric, DecimalPlaces = 5 },
    ];

    private static IEnumerable<ParamDescriptor> CalTriplet(byte c0Id, byte c1Id, byte tcId, string channel, string range, double c1Default) =>
    [
        new() { Id = c0Id, Name = $"CAL_{channel}_C0", Type = ParamValueType.F32, Min = -100.0, Max = 100.0, Default = 0.0, Unit = "V", Group = "Kalibracja ADC", Calibration = true,
            Description = $"{channel}: offset c0 [V] (zakres wejścia {range}). V_skor = c0 + c1·V_adc, z kompensacją temp.", Control = ParamControlKind.Numeric, DecimalPlaces = 4 },
        new() { Id = c1Id, Name = $"CAL_{channel}_C1", Type = ParamValueType.F32, Min = 0.0, Max = 1000.0, Default = c1Default, Unit = "-", Group = "Kalibracja ADC", Calibration = true,
            Description = $"{channel}: wzmocnienie c1 (odwrotność dzielnika rezystorowego).", Control = ParamControlKind.Numeric, DecimalPlaces = 4 },
        new() { Id = tcId, Name = $"CAL_{channel}_TC", Type = ParamValueType.F32, Min = -0.01, Max = 0.01, Default = 0.0, Unit = "1/°C", Group = "Kalibracja ADC", Calibration = true,
            Description = $"{channel}: współczynnik kompensacji temperaturowej tc (V *= 1 + tc·(T-Tref)).", Control = ParamControlKind.Numeric, DecimalPlaces = 5 },
    ];
}
