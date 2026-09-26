namespace CanSensorHub.Metrology.Instruments;

/// <summary>Źródło napięcia stałego sterowane programowo — zadajnik punktów kalibracji.</summary>
public interface IVoltageSource
{
    string Description { get; }

    /// <summary>Dopuszczalny zakres nastaw po uwzględnieniu ograniczeń przyrządu i operatora.</summary>
    double MinVoltage { get; }
    double MaxVoltage { get; }

    /// <summary>Ustawia napięcie. Wartość spoza [<see cref="MinVoltage"/>, <see cref="MaxVoltage"/>] jest odrzucana wyjątkiem, nigdy przycinana.</summary>
    Task SetVoltageAsync(double volts, CancellationToken ct = default);

    /// <summary>Stan bezpieczny: 0 V. Wywoływane także po anulowaniu, więc nie przyjmuje tokenu anulowania.</summary>
    Task SetZeroAsync();
}

/// <summary>Woltomierz wzorcowy — przyrząd, którego odczyt jest wartością odniesienia.</summary>
public interface IVoltmeter
{
    string Description { get; }

    /// <summary>Konfiguracja pomiaru napięcia stałego. Wywoływana raz, przed serią punktów.</summary>
    Task ConfigureAsync(CancellationToken ct = default);

    /// <summary>Seria <paramref name="count"/> kolejnych odczytów w woltach.</summary>
    Task<IReadOnlyList<double>> ReadSamplesAsync(int count, CancellationToken ct = default);
}
