using CanSensorHub.Core.Protocol;
using CanSensorHub.Modules.Mpcc.Protocol;

namespace CanSensorHub.Tools.MpccAfeCal.Calibration;

/// <summary>Jeden kanał AFE: jego parametry kalibracyjne w węźle i nominalny zakres wejścia.</summary>
public sealed record AfeChannelInfo(int Index, ParamDescriptor C0, ParamDescriptor C1, ParamDescriptor Tc, double NominalFullScaleVolts)
{
    public string Name => $"CH{Index}";
}

/// <summary>Współczynniki korekcji jednego kanału w modelu firmware: <c>V = k·(c1·V_pin + c0)</c>, <c>k = 1 + tc·(T − Tref)</c>.</summary>
public readonly record struct AfeCoefficients(double C0, double C1, double Tc);

/// <summary>Tablice AFE węzła MPCC, wyprowadzone z rejestru parametrów modułu MPCC.</summary>
public static class AfeModel
{
    public const int ChannelCount = 7;

    /// <summary>Pełna skala ADC na pinie: VDDA z referencji LM4040.</summary>
    public const double AdcFullScaleVolts = 3.0;

    /// <summary>Próg nasycenia: odczyt powyżej 99 % pełnej skali nie niesie informacji o napięciu wejściowym.</summary>
    public const double SaturationFraction = 0.99;

    /// <summary>
    /// Napięcie na pinie odtworzone z odczytu węzła przez odwrócenie korekcji. Służy wyłącznie do
    /// wykrycia nasycenia, więc czynnik temperaturowy jest pomijany.
    /// </summary>
    public static double EstimatePinVolts(double nodeVolts, AfeCoefficients c) =>
        c.C1 == 0 ? double.NaN : (nodeVolts - c.C0) / c.C1;

    public static bool IsSaturated(double nodeVolts, AfeCoefficients c) =>
        EstimatePinVolts(nodeVolts, c) >= SaturationFraction * AdcFullScaleVolts;

    public static ParamDescriptor Param(string name) =>
        MpccParams.All.FirstOrDefault(p => p.Name == name)
        ?? throw new InvalidOperationException($"Rejestr parametrów MPCC nie zawiera {name}.");

    public static ParamDescriptor CalLock { get; } = Param("CAL_LOCK");
    public static ParamDescriptor Tref { get; } = Param("CAL_ADC_TREF");
    public static ParamDescriptor MeasurePeriod { get; } = Param("MEASURE_PERIOD");

    /// <summary>Kanały 0–3: dzielnik 25k/15k, zakres ~5 V; kanały 4–6: dzielnik 20k/2k, zakres ~30 V.</summary>
    public static IReadOnlyList<AfeChannelInfo> Channels { get; } = Enumerable.Range(0, ChannelCount)
        .Select(i => new AfeChannelInfo(i, Param($"CAL_ADC{i}_C0"), Param($"CAL_ADC{i}_C1"), Param($"CAL_ADC{i}_TC"), i < 4 ? 5.0 : 30.0))
        .ToList();
}

public static class AfeCalibrationMath
{
    /// <summary>Czynnik kompensacji temperaturowej, który firmware stosuje po korekcji liniowej.</summary>
    public static double TemperatureFactor(double tc, double temperature, double tref) =>
        tc == 0.0 ? 1.0 : 1.0 + tc * (temperature - tref);

    /// <summary>
    /// Nowe współczynniki z dopasowania <c>V_ref = a·V_węzła + b</c> wykonanego na odczytach już
    /// skorygowanych przez bieżące współczynniki (docs/adr/0004):
    /// <c>c1' = a·c1</c>, <c>c0' = a·c0 + b/k</c>, <c>tc</c> bez zmian.
    /// </summary>
    /// <remarks>
    /// Z <c>V_węzła = k·(c1·V_pin + c0)</c> wymóg <c>k·(c1'·V_pin + c0') = a·V_węzła + b</c> daje
    /// powyższe wzory dla każdego <c>V_pin</c>. <paramref name="k"/> to czynnik temperaturowy
    /// w chwili pomiaru; bez kompensacji (<c>tc = 0</c>) wynosi 1.
    /// </remarks>
    public static AfeCoefficients Compose(AfeCoefficients current, double slope, double intercept, double k) =>
        new(C0: slope * current.C0 + intercept / k, C1: slope * current.C1, Tc: current.Tc);

    /// <summary>Wartość, którą węzeł faktycznie przechowa — firmware trzyma współczynniki jako f32.</summary>
    public static double AsStored(double value) => (float)value;
}
