namespace CanSensorHub.Metrology.Analysis;

/// <summary>Prosta <c>y = Slope·x + Intercept</c> wraz z residuami w punktach dopasowania.</summary>
public sealed record LinearFitResult(double Slope, double Intercept, IReadOnlyList<double> Residuals, double RSquared)
{
    public double Evaluate(double x) => Slope * x + Intercept;

    public double MaxAbsResidual => Residuals.Count == 0 ? 0 : Residuals.Max(Math.Abs);
}

public static class LinearFit
{
    /// <summary>
    /// Dopasowanie metodą najmniejszych kwadratów. Dla dwóch punktów prosta przechodzi dokładnie
    /// przez oba. Obliczenia na wartościach wycentrowanych — przy napięciach rzędu woltów i różnicach
    /// rzędu mikrowoltów sumy kwadratów w postaci naiwnej tracą cyfry znaczące.
    /// </summary>
    public static LinearFitResult Fit(IReadOnlyList<double> x, IReadOnlyList<double> y)
    {
        if (x.Count != y.Count) throw new ArgumentException("Różna liczba wartości x i y.");
        if (x.Count < 2) throw new ArgumentException("Dopasowanie prostej wymaga co najmniej dwóch punktów.");

        var mx = x.Average();
        var my = y.Average();
        double sxx = 0, sxy = 0, syy = 0;
        for (var i = 0; i < x.Count; i++)
        {
            var dx = x[i] - mx;
            var dy = y[i] - my;
            sxx += dx * dx;
            sxy += dx * dy;
            syy += dy * dy;
        }
        if (sxx <= 0) throw new ArgumentException("Wszystkie punkty mają tę samą wartość x — nachylenie nieokreślone.");

        var slope = sxy / sxx;
        var intercept = my - slope * mx;
        var residuals = x.Select((xi, i) => y[i] - (slope * xi + intercept)).ToList();
        var ssRes = residuals.Sum(r => r * r);
        var r2 = syy > 0 ? 1 - ssRes / syy : 1.0;
        return new LinearFitResult(slope, intercept, residuals, r2);
    }
}

public static class Setpoints
{
    /// <summary><paramref name="count"/> punktów równomiernie od <paramref name="min"/> do <paramref name="max"/> włącznie, zaokrąglonych do 1 mV.</summary>
    public static IReadOnlyList<double> Linear(double min, double max, int count)
    {
        if (count < 2) throw new ArgumentOutOfRangeException(nameof(count), "Wymagane co najmniej dwa punkty.");
        if (max <= min) throw new ArgumentException("Górna granica zakresu musi być większa od dolnej.");
        return Enumerable.Range(0, count).Select(i => Math.Round(min + (max - min) * i / (count - 1), 3)).ToList();
    }

    /// <summary>
    /// Punkty przesunięte o pół kroku względem <see cref="Linear"/> o tej samej liczności — sprawdzenie
    /// w punktach innych niż te, na których wyznaczono współczynniki, wykrywa nieliniowość, której
    /// dopasowanie w swoich własnych punktach nie ujawnia.
    /// </summary>
    public static IReadOnlyList<double> Interleaved(double min, double max, int count)
    {
        if (count < 1) throw new ArgumentOutOfRangeException(nameof(count), "Wymagany co najmniej jeden punkt.");
        if (max <= min) throw new ArgumentException("Górna granica zakresu musi być większa od dolnej.");
        var step = (max - min) / count;
        return Enumerable.Range(0, count).Select(i => Math.Round(min + step * (i + 0.5), 3)).ToList();
    }
}
