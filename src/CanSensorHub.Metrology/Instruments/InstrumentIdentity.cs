namespace CanSensorHub.Metrology.Instruments;

public enum InstrumentKind
{
    Unknown,
    /// <summary>Multimetr HP/Agilent 34401A.</summary>
    Dmm34401A,
    /// <summary>Jednostka akwizycji danych Agilent 34970A (także zgodny 34972A).</summary>
    Daq34970A,
}

/// <summary>Odpowiedź <c>*IDN?</c> (IEEE 488.2): producent, model, numer seryjny, wersja firmware.</summary>
public sealed record InstrumentIdentity(string Manufacturer, string Model, string Serial, string Firmware)
{
    public string Raw => string.Join(',', Manufacturer, Model, Serial, Firmware);

    public InstrumentKind Kind => Model.ToUpperInvariant() switch
    {
        var m when m.Contains("34401A") => InstrumentKind.Dmm34401A,
        var m when m.Contains("34970A") || m.Contains("34972A") => InstrumentKind.Daq34970A,
        _ => InstrumentKind.Unknown,
    };

    public override string ToString() => $"{Manufacturer} {Model} (S/N {Serial}, FW {Firmware})";

    public static InstrumentIdentity Parse(string idn)
    {
        var f = idn.Trim().Split(',', StringSplitOptions.TrimEntries);
        string At(int i) => i < f.Length ? f[i] : "";
        return new InstrumentIdentity(At(0), At(1), At(2), At(3));
    }
}

/// <summary>Wspólne operacje IEEE 488.2 na sesji SCPI.</summary>
public static class ScpiCommon
{
    public static async Task<InstrumentIdentity> IdentifyAsync(Visa.IScpiSession session, CancellationToken ct = default) =>
        InstrumentIdentity.Parse(await session.QueryAsync("*IDN?", ct: ct).ConfigureAwait(false));

    /// <summary>
    /// Opróżnia kolejkę błędów przyrządu i zgłasza pierwszy z nich. Komendy SCPI nie potwierdzają
    /// wykonania, więc bez tej kontroli odrzucona nastawa (np. napięcie poza zakresem) wyglądałaby
    /// jak przyjęta.
    /// </summary>
    public static async Task ThrowOnErrorAsync(Visa.IScpiSession session, string context, CancellationToken ct = default)
    {
        var errors = new List<string>();
        for (var i = 0; i < 20; i++)
        {
            var err = (await session.QueryAsync("SYST:ERR?", ct: ct).ConfigureAwait(false)).Trim();
            if (err.StartsWith("+0", StringComparison.Ordinal) || err.StartsWith("0,", StringComparison.Ordinal)) break;
            errors.Add(err);
        }
        if (errors.Count > 0)
            throw new Visa.InstrumentException($"{session.ResourceName}: {context} — przyrząd zgłosił: {string.Join("; ", errors)}");
    }
}
