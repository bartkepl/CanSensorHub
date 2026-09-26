namespace CanSensorHub.Metrology.Analysis;

/// <summary>Statystyka serii odczytów jednej wielkości w jednym punkcie pomiarowym.</summary>
public readonly record struct SampleStats(int Count, double Mean, double StdDev, double Min, double Max)
{
    /// <summary>Niepewność standardowa średniej (typ A): σ/√n.</summary>
    public double StdError => Count > 0 ? StdDev / Math.Sqrt(Count) : double.NaN;

    /// <summary>Odchylenie standardowe z próby (n − 1); dla jednej próbki 0.</summary>
    public static SampleStats From(IEnumerable<double> samples)
    {
        var xs = samples as IReadOnlyList<double> ?? samples.ToList();
        if (xs.Count == 0) throw new ArgumentException("Seria bez próbek.", nameof(samples));
        var mean = xs.Average();
        var sd = xs.Count > 1 ? Math.Sqrt(xs.Sum(x => (x - mean) * (x - mean)) / (xs.Count - 1)) : 0.0;
        return new SampleStats(xs.Count, mean, sd, xs.Min(), xs.Max());
    }
}
