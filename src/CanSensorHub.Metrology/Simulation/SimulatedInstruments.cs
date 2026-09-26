using CanSensorHub.Metrology.Instruments;

namespace CanSensorHub.Metrology.Simulation;

/// <summary>
/// Wirtualny punkt połączeniowy stanowiska: zadajnik ustawia na nim napięcie, a woltomierz
/// i symulowany węzeł je odczytują. <c>null</c> oznacza, że nic nie wymusza napięcia.
/// </summary>
public interface IAnalogNode
{
    double? Voltage { get; set; }
}

/// <summary>Samodzielny punkt połączeniowy, gdy symulowany węzeł nie uczestniczy (np. w testach przyrządów).</summary>
public sealed class AnalogNode : IAnalogNode
{
    private readonly Lock _gate = new();
    private double? _voltage;

    public double? Voltage
    {
        get { lock (_gate) return _voltage; }
        set { lock (_gate) _voltage = value; }
    }
}

/// <summary>Idealny zadajnik: napięcie na punkcie połączeniowym równe nastawie z dokładnością rozdzielczości DAC.</summary>
public sealed class SimulatedVoltageSource(IAnalogNode node, double minVoltage, double maxVoltage) : IVoltageSource
{
    public string Description => "Zadajnik symulowany";
    public double MinVoltage { get; } = minVoltage;
    public double MaxVoltage { get; } = maxVoltage;

    public Task SetVoltageAsync(double volts, CancellationToken ct = default)
    {
        if (volts < MinVoltage - 1e-9 || volts > MaxVoltage + 1e-9)
            throw new ArgumentOutOfRangeException(nameof(volts), $"Napięcie {volts:0.000} V poza zakresem {MinVoltage:0.000}..{MaxVoltage:0.000} V.");
        node.Voltage = Math.Round(volts, 3);
        return Task.CompletedTask;
    }

    public Task SetZeroAsync()
    {
        node.Voltage = 0.0;
        return Task.CompletedTask;
    }
}

/// <summary>Woltomierz symulowany z szumem gaussowskim o zadanym odchyleniu.</summary>
public sealed class SimulatedVoltmeter(IAnalogNode node, double noiseSigmaVolts = 5e-6, int seed = 1) : IVoltmeter
{
    private readonly Random _rng = new(seed);

    public string Description => "Woltomierz symulowany";

    public Task ConfigureAsync(CancellationToken ct = default) => Task.CompletedTask;

    public Task<IReadOnlyList<double>> ReadSamplesAsync(int count, CancellationToken ct = default)
    {
        var v = node.Voltage ?? 0.0;
        IReadOnlyList<double> samples = Enumerable.Range(0, count).Select(_ => v + noiseSigmaVolts * Gaussian(_rng)).ToList();
        return Task.FromResult(samples);
    }

    internal static double Gaussian(Random rng)
    {
        // Box–Muller; 1 - NextDouble() wyklucza log(0).
        var u1 = 1.0 - rng.NextDouble();
        var u2 = rng.NextDouble();
        return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
    }
}
