using System.Globalization;
using CanSensorHub.Metrology.Visa;

namespace CanSensorHub.Metrology.Instruments;

/// <summary>Ustawienia pomiaru DCV multimetru 34401A.</summary>
public sealed record Dmm34401ASettings
{
    /// <summary>Zakres w woltach: 0.1, 1, 10, 100 albo 1000. Stały zakres — autozakres zmieniałby się między punktami.</summary>
    public double RangeVolts { get; init; } = 10;

    /// <summary>Czas całkowania w cyklach sieci: 0.02, 0.2, 1, 10 albo 100. Od 10 wzwyż przyrząd tłumi przydźwięk sieci.</summary>
    public double Nplc { get; init; } = 10;

    /// <summary>Automatyczne zerowanie przed każdym odczytem — kompensuje dryf offsetu kosztem podwojenia czasu odczytu.</summary>
    public bool AutoZero { get; init; } = true;

    /// <summary>
    /// Impedancja wejściowa powyżej 10 GΩ na zakresach do 10 V (<c>INP:IMP:AUTO ON</c>). Domyślne 10 MΩ
    /// obciąża mierzony węzeł; przy dzielnikach rzędu kiloomów błąd jest pomijalny, ale nie zerowy.
    /// </summary>
    public bool HighImpedance { get; init; } = true;

    public static IReadOnlyList<double> AllowedRanges { get; } = [0.1, 1, 10, 100, 1000];
    public static IReadOnlyList<double> AllowedNplc { get; } = [0.02, 0.2, 1, 10, 100];
}

/// <summary>
/// HP/Agilent 34401A jako woltomierz wzorcowy. Seria próbek jest pobierana jednym wyzwoleniem
/// (<c>SAMP:COUN</c> + <c>READ?</c>), więc odstęp między próbkami wyznacza przyrząd, a nie
/// opóźnienia magistrali GPIB.
/// </summary>
public sealed class Dmm34401A(IScpiSession session, Dmm34401ASettings settings) : IVoltmeter
{
    /// <summary>Pojemność pamięci odczytów przyrządu.</summary>
    public const int MaxSamplesPerTrigger = 512;

    public Dmm34401ASettings Settings { get; } = settings;
    public string Description => $"34401A ({session.ResourceName})";

    public async Task ConfigureAsync(CancellationToken ct = default)
    {
        if (!Dmm34401ASettings.AllowedRanges.Contains(Settings.RangeVolts))
            throw new ArgumentOutOfRangeException(nameof(Settings.RangeVolts), $"Niedozwolony zakres {Settings.RangeVolts} V.");
        if (!Dmm34401ASettings.AllowedNplc.Contains(Settings.Nplc))
            throw new ArgumentOutOfRangeException(nameof(Settings.Nplc), $"Niedozwolona wartość NPLC {Settings.Nplc}.");

        await session.WriteAsync("*CLS", ct).ConfigureAwait(false);
        await session.WriteAsync($"CONF:VOLT:DC {F(Settings.RangeVolts)}", ct).ConfigureAwait(false);
        await session.WriteAsync($"VOLT:DC:NPLC {F(Settings.Nplc)}", ct).ConfigureAwait(false);
        await session.WriteAsync($"ZERO:AUTO {(Settings.AutoZero ? "ON" : "OFF")}", ct).ConfigureAwait(false);
        await session.WriteAsync($"INP:IMP:AUTO {(Settings.HighImpedance ? "ON" : "OFF")}", ct).ConfigureAwait(false);
        await session.WriteAsync("TRIG:SOUR IMM", ct).ConfigureAwait(false);
        await session.WriteAsync("TRIG:DEL:AUTO ON", ct).ConfigureAwait(false);
        await ScpiCommon.ThrowOnErrorAsync(session, "konfiguracja DCV", ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<double>> ReadSamplesAsync(int count, CancellationToken ct = default)
    {
        if (count is < 1 or > MaxSamplesPerTrigger)
            throw new ArgumentOutOfRangeException(nameof(count), $"Liczba próbek musi mieścić się w 1..{MaxSamplesPerTrigger}.");

        await session.WriteAsync($"SAMP:COUN {count}", ct).ConfigureAwait(false);
        var response = await session.QueryAsync("READ?", EstimateDuration(count), ct).ConfigureAwait(false);
        var values = ParseReadings(response);
        if (values.Count != count)
            throw new InstrumentException($"{session.ResourceName}: oczekiwano {count} odczytów, otrzymano {values.Count}.");
        return values;
    }

    /// <summary>Limit czasu serii: czas całkowania (podwojony przy autozerowaniu) przy sieci 50 Hz, z zapasem na przesył.</summary>
    public TimeSpan EstimateDuration(int count)
    {
        var perSample = Settings.Nplc / 50.0 * (Settings.AutoZero ? 2 : 1) + 0.01;
        return TimeSpan.FromSeconds(count * perSample * 1.5 + 3);
    }

    internal static IReadOnlyList<double> ParseReadings(string response) =>
        response.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => double.Parse(s, NumberStyles.Float, CultureInfo.InvariantCulture))
            .ToList();

    private static string F(double v) => v.ToString("G", CultureInfo.InvariantCulture);
}
