using System.Globalization;
using CanSensorHub.Metrology.Visa;

namespace CanSensorHub.Metrology.Instruments;

/// <summary>
/// Agilent 34970A — w zakresie potrzebnym narzędziom kalibracyjnym: rozpoznanie kart
/// w gniazdach i wyjścia analogowe karty wielofunkcyjnej 34907A.
/// </summary>
/// <remarks>
/// Kanały karty 34907A w gnieździe <c>s</c> (100, 200, 300): <c>s01</c>/<c>s02</c> — porty cyfrowe,
/// <c>s03</c> — totalizator, <c>s04</c> — DAC1, <c>s05</c> — DAC2. Wysłanie <c>SOUR:VOLT</c> na kanał
/// portu cyfrowego kończy się błędem przyrządu, a nie ustawieniem napięcia.
/// </remarks>
public sealed class Daq34970A(IScpiSession session)
{
    public static IReadOnlyList<int> Slots { get; } = [100, 200, 300];

    /// <summary>Zakres wyjścia DAC karty 34907A.</summary>
    public const double DacMinVolts = -12.0;
    public const double DacMaxVolts = 12.0;

    /// <summary>Rozdzielczość DAC karty 34907A (16 bitów na ±12 V, nastawa w krokach 1 mV).</summary>
    public const double DacResolutionVolts = 0.001;

    public IScpiSession Session => session;

    /// <summary>Model karty w każdym gnieździe (<c>SYST:CTYP?</c>); pusty ciąg dla gniazda bez karty.</summary>
    public async Task<IReadOnlyDictionary<int, string>> GetCardModelsAsync(CancellationToken ct = default)
    {
        var result = new Dictionary<int, string>();
        foreach (var slot in Slots)
        {
            var idn = InstrumentIdentity.Parse(await session.QueryAsync($"SYST:CTYP? {slot}", ct: ct).ConfigureAwait(false));
            // Puste gniazdo raportuje model "0".
            result[slot] = idn.Model == "0" ? "" : idn.Model;
        }
        return result;
    }

    public static int DacChannel(int slot, int dac)
    {
        if (!Slots.Contains(slot)) throw new ArgumentOutOfRangeException(nameof(slot), "Gniazdo musi być 100, 200 albo 300.");
        if (dac is not (1 or 2)) throw new ArgumentOutOfRangeException(nameof(dac), "Wyjście DAC musi być 1 albo 2.");
        return slot + (dac == 1 ? 4 : 5);
    }

    public async Task SetDacAsync(int slot, int dac, double volts, CancellationToken ct = default)
    {
        var channel = DacChannel(slot, dac);
        if (double.IsNaN(volts) || volts < DacMinVolts || volts > DacMaxVolts)
            throw new ArgumentOutOfRangeException(nameof(volts), $"Napięcie {volts} V poza zakresem DAC {DacMinVolts}..{DacMaxVolts} V.");
        var v = Math.Round(volts, 3).ToString("0.000", CultureInfo.InvariantCulture);
        await session.WriteAsync($"SOUR:VOLT {v},(@{channel})", ct).ConfigureAwait(false);
        await ScpiCommon.ThrowOnErrorAsync(session, $"nastawa DAC{dac} (kanał {channel})", ct).ConfigureAwait(false);
    }

    public async Task<double> ReadDacAsync(int slot, int dac, CancellationToken ct = default)
    {
        var r = await session.QueryAsync($"SOUR:VOLT? (@{DacChannel(slot, dac)})", ct: ct).ConfigureAwait(false);
        return double.Parse(r.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture);
    }
}

/// <summary>
/// Jedno wyjście DAC karty 34907A jako źródło napięcia, z zakresem zawężonym przez operatora —
/// np. do 5 V, gdy do wyjścia dołączone są wejścia o takim zakresie.
/// </summary>
public sealed class Dac34907AChannel : IVoltageSource
{
    private readonly Daq34970A _daq;

    public int Slot { get; }
    public int Dac { get; }
    public double MinVoltage { get; }
    public double MaxVoltage { get; }
    public string Description => $"34907A gniazdo {Slot} DAC{Dac}, kanał {Daq34970A.DacChannel(Slot, Dac)} ({_daq.Session.ResourceName})";

    public Dac34907AChannel(Daq34970A daq, int slot, int dac, double operatorMin, double operatorMax)
    {
        Daq34970A.DacChannel(slot, dac); // walidacja adresu
        if (operatorMin > operatorMax) throw new ArgumentException("Dolna granica zakresu większa od górnej.");
        _daq = daq;
        Slot = slot;
        Dac = dac;
        MinVoltage = Math.Max(operatorMin, Daq34970A.DacMinVolts);
        MaxVoltage = Math.Min(operatorMax, Daq34970A.DacMaxVolts);
    }

    public Task SetVoltageAsync(double volts, CancellationToken ct = default)
    {
        if (double.IsNaN(volts) || volts < MinVoltage - 1e-9 || volts > MaxVoltage + 1e-9)
            throw new ArgumentOutOfRangeException(nameof(volts), $"Napięcie {volts:0.000} V poza dopuszczalnym zakresem {MinVoltage:0.000}..{MaxVoltage:0.000} V.");
        return _daq.SetDacAsync(Slot, Dac, volts, ct);
    }

    public Task SetZeroAsync() => _daq.SetDacAsync(Slot, Dac, 0.0);
}
