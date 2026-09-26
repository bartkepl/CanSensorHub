namespace CanSensorHub.Metrology.Analysis;

/// <summary>
/// Odrzucanie próbek odstających metodą odporną: odległość od mediany porównywana z odchyleniem
/// oszacowanym z MAD (medianowe odchylenie bezwzględne), a nie ze średnią i σ serii — pojedyncza
/// próbka odstająca zawyża σ na tyle, że sama by się w niej zmieściła.
/// </summary>
public static class OutlierFilter
{
    /// <summary>Współczynnik zamiany MAD na σ dla rozkładu normalnego.</summary>
    private const double MadToSigma = 1.4826;

    /// <param name="samples">Seria odczytów.</param>
    /// <param name="minThreshold">Najmniejszy próg odrzucenia, w jednostkach próbek. Chroni serie skwantowane,
    /// w których większość odczytów jest identyczna, a MAD wynosi zero.</param>
    /// <param name="k">Próg w wielokrotnościach σ oszacowanego z MAD.</param>
    public static (IReadOnlyList<double> Kept, IReadOnlyList<double> Rejected) Apply(
        IReadOnlyList<double> samples, double minThreshold, double k = 6.0)
    {
        if (samples.Count < 3) return (samples, []);
        var median = Median(samples);
        var mad = Median(samples.Select(x => Math.Abs(x - median)).ToList());
        var threshold = Math.Max(k * MadToSigma * mad, minThreshold);
        var kept = samples.Where(x => Math.Abs(x - median) <= threshold).ToList();
        var rejected = samples.Where(x => Math.Abs(x - median) > threshold).ToList();
        return (kept, rejected);
    }

    public static double Median(IReadOnlyList<double> xs)
    {
        if (xs.Count == 0) throw new ArgumentException("Seria bez próbek.", nameof(xs));
        var sorted = xs.Order().ToArray();
        var mid = sorted.Length / 2;
        return sorted.Length % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2.0;
    }
}
