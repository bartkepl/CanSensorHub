using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CanSensorHub.Tools.MpccAfeCal.Calibration;

public enum CalibrationMode
{
    /// <summary>Dwa punkty na krańcach zakresu; prosta wyznaczona dokładnie.</summary>
    TwoPoint,
    /// <summary>Wiele punktów, prosta metodą najmniejszych kwadratów; residua ujawniają nieliniowość.</summary>
    MultiPoint,
}

/// <summary>Ustawienia narzędzia, zapamiętywane między uruchomieniami.</summary>
public sealed class AfeCalSettings
{
    // --- przyrządy ---
    public string? DmmResource { get; set; }
    public string? DaqResource { get; set; }
    public int DacSlot { get; set; } = 200;
    public int DacOutput { get; set; } = 1;

    // --- zakres i punkty ---
    /// <summary>
    /// Zakres zadawanych napięć. Górna granica wynika z najmniejszego zakresu dołączonych wejść:
    /// na kanałach 0–3 napięcie 5 V daje na pinie ~3,0 V, czyli pełną skalę ADC (VDDA ≈ 3,0 V),
    /// stąd domyślnie 4,8 V z zapasem na tolerancję dzielnika.
    /// </summary>
    public double MinVoltage { get; set; } = 0.1;
    public double MaxVoltage { get; set; } = 4.8;
    public CalibrationMode Mode { get; set; } = CalibrationMode.MultiPoint;
    public int CalibrationPoints { get; set; } = 5;
    public int CheckPoints { get; set; } = 6;
    public bool[] EnabledChannels { get; set; } = [true, true, true, true, true, true, true];

    // --- uśrednianie i stabilność ---
    public int DmmSamples { get; set; } = 10;
    public double DmmNplc { get; set; } = 10;
    public bool DmmAutoZero { get; set; } = true;
    public int NodeRounds { get; set; } = 10;
    /// <summary>Odstęp rund odczytu węzła w ms; 0 — okres pomiaru węzła (<c>MEASURE_PERIOD</c>).</summary>
    public int NodeRoundIntervalMs { get; set; }
    /// <summary>Czas ustalania po zmianie nastawy w ms; 0 — dwa okresy pomiaru węzła plus 500 ms.</summary>
    public int SettleMs { get; set; }
    /// <summary>Największe dopuszczalne odchylenie standardowe odczytów wzorca; powyżej punkt jest powtarzany.</summary>
    public double MaxReferenceStdDevMv { get; set; } = 0.5;
    public int MaxAttempts { get; set; } = 3;

    // --- akceptacja ---
    /// <summary>Tolerancja błędu kanału w sprawdzeniu.</summary>
    public double ToleranceMv { get; set; } = 5.0;
    /// <summary>Największa dopuszczalna korekcja wzmocnienia |a − 1|; większa wskazuje zwykle błąd połączeń, nie dzielnika.</summary>
    public double MaxGainCorrection { get; set; } = 0.05;
    /// <summary>Największa dopuszczalna korekcja przesunięcia |b| w mV.</summary>
    public double MaxOffsetCorrectionMv { get; set; } = 100;

    public IReadOnlyList<int> EnabledChannelIndices =>
        Enumerable.Range(0, AfeModel.ChannelCount).Where(i => i < EnabledChannels.Length && EnabledChannels[i]).ToList();

    public AfeCalSettings Clone() => JsonSerializer.Deserialize(JsonSerializer.Serialize(this, AfeCalSettingsJson.Default.AfeCalSettings), AfeCalSettingsJson.Default.AfeCalSettings)!;

    /// <summary>Sprawdza spójność ustawień; zwraca opis pierwszej niezgodności albo <c>null</c>.</summary>
    public string? Validate()
    {
        if (MaxVoltage <= MinVoltage) return "Górna granica zakresu musi być większa od dolnej.";
        if (MinVoltage < -12 || MaxVoltage > 12) return "Zakres wykracza poza ±12 V wyjścia DAC karty 34907A.";
        if (MinVoltage < 0) return "Wejścia AFE nie mierzą napięć ujemnych — dolna granica musi być ≥ 0 V.";
        if (Mode == CalibrationMode.MultiPoint && CalibrationPoints < 3) return "Kalibracja wielopunktowa wymaga co najmniej 3 punktów.";
        if (CheckPoints < 1) return "Sprawdzenie wymaga co najmniej jednego punktu.";
        if (DmmSamples is < 1 or > 512) return "Liczba próbek multimetru musi mieścić się w 1..512.";
        if (NodeRounds < 1) return "Liczba rund odczytu węzła musi być dodatnia.";
        if (EnabledChannelIndices.Count == 0) return "Nie wybrano żadnego kanału.";
        if (ToleranceMv <= 0) return "Tolerancja musi być dodatnia.";
        return null;
    }
}

[JsonSourceGenerationOptions(WriteIndented = true, UseStringEnumConverter = true)]
[JsonSerializable(typeof(AfeCalSettings))]
internal partial class AfeCalSettingsJson : JsonSerializerContext;

/// <summary>Trwałość ustawień w <c>%AppData%\CanSensorHub\tools\mpcc-afe-cal.json</c>.</summary>
public static class AfeCalSettingsStore
{
    public static string DataDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CanSensorHub");

    private static string FilePath => Path.Combine(DataDirectory, "tools", "mpcc-afe-cal.json");

    public static string ReportDirectory => Path.Combine(DataDirectory, "calibration");

    public static AfeCalSettings Load()
    {
        try
        {
            return File.Exists(FilePath)
                ? JsonSerializer.Deserialize(File.ReadAllText(FilePath), AfeCalSettingsJson.Default.AfeCalSettings) ?? new()
                : new();
        }
        catch
        {
            return new();
        }
    }

    public static void Save(AfeCalSettings settings)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(settings, AfeCalSettingsJson.Default.AfeCalSettings));
        }
        catch
        {
            // Utrata ustawień między uruchomieniami nie wpływa na wynik kalibracji.
        }
    }
}
