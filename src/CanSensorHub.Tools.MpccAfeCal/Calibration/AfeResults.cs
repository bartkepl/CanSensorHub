using CanSensorHub.Metrology.Analysis;

namespace CanSensorHub.Tools.MpccAfeCal.Calibration;

/// <summary>
/// Wynik jednego punktu: nastawa, seria wzorca i seria każdego kanału węzła (w woltach). Kanał
/// wyłączony albo bez ważnych próbek ma wartość <c>null</c>.
/// </summary>
public sealed record PointMeasurement(
    int Index,
    double Setpoint,
    SampleStats Reference,
    IReadOnlyList<SampleStats?> Channels,
    double? BoardTemperature,
    DateTimeOffset Time,
    int Attempts);

/// <summary>Wyznaczone współczynniki jednego kanału wraz z oceną ich wiarygodności.</summary>
public sealed record ChannelCalibration(
    int Channel,
    AfeCoefficients Before,
    AfeCoefficients After,
    LinearFitResult Fit,
    double TemperatureFactor,
    string? Rejection)
{
    public bool Accepted => Rejection is null;
    /// <summary>Residua dopasowania w mV — przewidywany błąd kanału po zapisie, w punktach kalibracji.</summary>
    public IReadOnlyList<double> ResidualsMv => Fit.Residuals.Select(r => r * 1000.0).ToList();
}

/// <summary>Jeden punkt sprawdzenia jednego kanału: błąd węzła względem wzorca.</summary>
public readonly record struct CheckPoint(double Reference, double Node, double ErrorMv);

public sealed record ChannelCheck(int Channel, IReadOnlyList<CheckPoint> Points, double ToleranceMv, int SaturatedPoints = 0)
{
    public double MaxAbsErrorMv => Points.Count == 0 ? double.NaN : Points.Max(p => Math.Abs(p.ErrorMv));
    public bool Pass => Points.Count > 0 && MaxAbsErrorMv <= ToleranceMv;
}

public static class AfeCalibrationCalculator
{
    /// <summary>
    /// Wyznacza współczynniki każdego kanału z punktów kalibracji. Dopasowanie odbywa się
    /// w dziedzinie odczytów węzła (<c>x</c> — węzeł, <c>y</c> — wzorzec), czyli dokładnie tej,
    /// w której firmware stosuje korekcję.
    /// </summary>
    public static IReadOnlyList<ChannelCalibration> Compute(
        IReadOnlyList<PointMeasurement> points,
        IReadOnlyDictionary<int, AfeCoefficients> current,
        double tref,
        AfeCalSettings settings)
    {
        var result = new List<ChannelCalibration>();
        foreach (var ch in settings.EnabledChannelIndices)
        {
            if (!current.TryGetValue(ch, out var before)) continue;
            var usable = points.Where(p => p.Channels[ch] is { } s && !AfeModel.IsSaturated(s.Mean, before)).ToList();
            if (usable.Count < 2) continue;

            var fit = LinearFit.Fit(usable.Select(p => p.Channels[ch]!.Value.Mean).ToList(), usable.Select(p => p.Reference.Mean).ToList());
            var temps = usable.Where(p => p.BoardTemperature.HasValue).Select(p => p.BoardTemperature!.Value).ToList();
            var k = temps.Count > 0 ? AfeCalibrationMath.TemperatureFactor(before.Tc, temps.Average(), tref) : 1.0;
            var composed = AfeCalibrationMath.Compose(before, fit.Slope, fit.Intercept, k);
            var after = new AfeCoefficients(AfeCalibrationMath.AsStored(composed.C0), AfeCalibrationMath.AsStored(composed.C1), before.Tc);

            result.Add(new ChannelCalibration(ch, before, after, fit, k, Judge(ch, fit, after, temps.Count > 0 || before.Tc == 0, settings)));
        }
        return result;
    }

    private static string? Judge(int ch, LinearFitResult fit, AfeCoefficients after, bool temperatureKnown, AfeCalSettings s)
    {
        var info = AfeModel.Channels[ch];
        // Residuum większe od tolerancji oznacza, że tor nie jest liniowy w zakresie kalibracji
        // (np. upływ zabezpieczenia wejścia albo nasycenie poniżej pełnej skali ADC). Korekcja
        // liniowa nie usunie takiego błędu, a nachylenie dopasowane do krzywej jest fałszywe.
        if (fit.Residuals.Count > 2 && fit.MaxAbsResidual * 1000.0 > s.ToleranceMv)
            return $"tor nieliniowy w zakresie: residuum {fit.MaxAbsResidual * 1000.0:0.0} mV > tolerancja {s.ToleranceMv:0.###} mV — zawęzić zakres albo sprawdzić obwód wejściowy";
        if (Math.Abs(fit.Slope - 1.0) > s.MaxGainCorrection)
            return $"korekcja wzmocnienia {(fit.Slope - 1.0) * 100:+0.00;-0.00} % przekracza {s.MaxGainCorrection * 100:0.##} % — sprawdzić połączenie kanału";
        if (Math.Abs(fit.Intercept) * 1000.0 > s.MaxOffsetCorrectionMv)
            return $"korekcja przesunięcia {fit.Intercept * 1000.0:+0.0;-0.0} mV przekracza {s.MaxOffsetCorrectionMv:0.#} mV";
        if (after.C0 < info.C0.Min || after.C0 > info.C0.Max || after.C1 < info.C1.Min || after.C1 > info.C1.Max)
            return "współczynnik poza zakresem parametru w węźle";
        if (!temperatureKnown)
            return "kanał ma kompensację temperaturową (tc ≠ 0), a temperatura płytki nie była dostępna";
        return null;
    }

    /// <summary>
    /// Ocena sprawdzenia: błąd każdego kanału w każdym punkcie względem wzorca. Punkty w nasyceniu
    /// ADC są liczone osobno — ich błąd opisuje zakres przetwornika, nie jakość kalibracji.
    /// </summary>
    public static IReadOnlyList<ChannelCheck> Evaluate(
        IReadOnlyList<PointMeasurement> points, IReadOnlyDictionary<int, AfeCoefficients> coefficients, AfeCalSettings settings)
    {
        var result = new List<ChannelCheck>();
        foreach (var ch in settings.EnabledChannelIndices)
        {
            var measured = points.Where(p => p.Channels[ch] is not null).ToList();
            var saturated = coefficients.TryGetValue(ch, out var c)
                ? measured.Where(p => AfeModel.IsSaturated(p.Channels[ch]!.Value.Mean, c)).ToList()
                : [];
            var checkPoints = measured.Except(saturated)
                .Select(p => new CheckPoint(p.Reference.Mean, p.Channels[ch]!.Value.Mean, (p.Channels[ch]!.Value.Mean - p.Reference.Mean) * 1000.0))
                .ToList();
            result.Add(new ChannelCheck(ch, checkPoints, settings.ToleranceMv, saturated.Count));
        }
        return result;
    }
}
