using System.Reflection;
using CanSensorHub.Core.Protocol;
using CanSensorHub.Metrology.Reporting;
using CanSensorHub.Tools.MpccAfeCal.Calibration;

namespace CanSensorHub.Tools.MpccAfeCal.Reporting;

/// <summary>Wszystko, co opisuje jedną sesję narzędzia: kontekst, dane surowe i wyniki.</summary>
public sealed class AfeCalibrationSession
{
    public DateTimeOffset Started { get; } = DateTimeOffset.Now;
    public required byte NodeId { get; init; }
    public DeviceIdentity? Node { get; set; }
    public string? ReferenceInstrument { get; set; }
    public string? SourceInstrument { get; set; }
    public bool Simulated { get; set; }
    public required AfeCalSettings Settings { get; init; }
    public double Tref { get; set; }
    public double MeasurePeriodMs { get; set; }
    public IReadOnlyDictionary<int, AfeCoefficients> CoefficientsBefore { get; set; } = new Dictionary<int, AfeCoefficients>();
    public IReadOnlyList<PointMeasurement> CalibrationPoints { get; set; } = [];
    public IReadOnlyList<ChannelCalibration> Calibrations { get; set; } = [];
    /// <summary>Rodzaj operacji zapisu: kalibracja albo przywrócenie wartości domyślnych.</summary>
    public string Operation { get; set; } = "kalibracja";
    public IReadOnlyList<CoefficientChange> Changes { get; set; } = [];
    public WriteOutcome? Write { get; set; }
    public IReadOnlyDictionary<int, AfeCoefficients>? CheckCoefficients { get; set; }
    public IReadOnlyList<PointMeasurement> CheckPoints { get; set; } = [];
    public IReadOnlyList<ChannelCheck> Checks { get; set; } = [];

    public string NodeLabel => Node?.UidHex ?? $"node{NodeId:X2}";
}

/// <summary>
/// Zapis sesji do pliku CSV z sekcjami. Raport zawiera dane surowe (statystyki serii w każdym
/// punkcie), a nie tylko wynik — pozwala to przeliczyć kalibrację innym modelem bez powtarzania
/// pomiarów.
/// </summary>
public static class AfeCalibrationReport
{
    public static string DefaultFileName(AfeCalibrationSession s) =>
        $"MPCC-AFE_{s.NodeLabel}_{s.Started:yyyyMMdd-HHmmss}.csv";

    public static void Write(string path, AfeCalibrationSession s)
    {
        using var w = new CsvReportWriter(path);
        var ver = typeof(AfeCalibrationReport).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

        w.Section("Sesja");
        w.KeyValue("narzedzie", $"CanSensorHub.Tools.MpccAfeCal {ver}");
        w.KeyValue("rozpoczecie", s.Started);
        w.KeyValue("tryb", s.Simulated ? "symulacja" : "stanowisko");
        w.KeyValue("operacja", s.Operation);
        w.KeyValue("wezel_node", $"0x{s.NodeId:X2}");
        if (s.Node is { } n)
        {
            w.KeyValue("wezel_uid", n.UidHex);
            w.KeyValue("wezel_fw", $"{n.FwMajor}.{n.FwMinor} build {n.BuildRevision}");
            w.KeyValue("wezel_hw", $"{n.HwMajor}.{n.HwMinor}");
        }
        w.KeyValue("wzorzec", s.ReferenceInstrument);
        w.KeyValue("zadajnik", s.SourceInstrument);
        w.KeyValue("tref_C", s.Tref);
        w.KeyValue("measure_period_ms", s.MeasurePeriodMs);

        var st = s.Settings;
        w.Section("Ustawienia");
        w.KeyValue("zakres_min_V", st.MinVoltage);
        w.KeyValue("zakres_max_V", st.MaxVoltage);
        w.KeyValue("tryb_kalibracji", st.Mode);
        w.KeyValue("punkty_kalibracji", st.Mode == CalibrationMode.TwoPoint ? 2 : st.CalibrationPoints);
        w.KeyValue("punkty_sprawdzenia", st.CheckPoints);
        w.KeyValue("kanaly", string.Join(' ', st.EnabledChannelIndices.Select(i => $"CH{i}")));
        w.KeyValue("dmm_probki", st.DmmSamples);
        w.KeyValue("dmm_nplc", st.DmmNplc);
        w.KeyValue("dmm_autozero", st.DmmAutoZero);
        w.KeyValue("wezel_rundy", st.NodeRounds);
        w.KeyValue("tolerancja_mV", st.ToleranceMv);

        WriteCoefficients(w, "Wspolczynniki przed", s.CoefficientsBefore);
        WritePoints(w, "Punkty kalibracji", s.CalibrationPoints);

        if (s.Calibrations.Count > 0)
        {
            w.Section("Dopasowanie");
            w.Row("kanal", "a", "b_V", "R2", "max_residuum_mV", "k", "c0_przed", "c1_przed", "c0_po", "c1_po", "status");
            foreach (var c in s.Calibrations)
                w.Row($"CH{c.Channel}", c.Fit.Slope, c.Fit.Intercept, c.Fit.RSquared, c.Fit.MaxAbsResidual * 1000.0, c.TemperatureFactor,
                    c.Before.C0, c.Before.C1, c.After.C0, c.After.C1, c.Rejection ?? "przyjety");
        }

        if (s.Changes.Count > 0)
        {
            w.Section("Zmiany wspolczynnikow");
            w.Row("kanal", "c0_przed", "c1_przed", "tc_przed", "c0_po", "c1_po", "tc_po");
            foreach (var c in s.Changes)
                w.Row($"CH{c.Channel}", c.Before.C0, c.Before.C1, c.Before.Tc, c.After.C0, c.After.C1, c.After.Tc);
        }

        if (s.Write is { } wr)
        {
            w.Section("Zapis do wezla");
            w.KeyValue("wynik", wr.Success ? "zapisano" : "niepowodzenie");
            foreach (var line in wr.Log) w.Row("", line);
        }

        if (s.CheckCoefficients is { } cc) WriteCoefficients(w, "Wspolczynniki podczas sprawdzenia", cc);
        WritePoints(w, "Punkty sprawdzenia", s.CheckPoints);

        if (s.Checks.Count > 0)
        {
            w.Section("Sprawdzenie");
            w.Row("kanal", "max_blad_mV", "tolerancja_mV", "punkty_w_nasyceniu", "wynik");
            foreach (var c in s.Checks)
                w.Row($"CH{c.Channel}", c.MaxAbsErrorMv, c.ToleranceMv, c.SaturatedPoints, c.Points.Count == 0 ? "brak danych" : c.Pass ? "PASS" : "FAIL");
        }
    }

    private static void WriteCoefficients(CsvReportWriter w, string title, IReadOnlyDictionary<int, AfeCoefficients> coefficients)
    {
        if (coefficients.Count == 0) return;
        w.Section(title);
        w.Row("kanal", "c0_V", "c1", "tc_1/C");
        foreach (var (ch, c) in coefficients.OrderBy(kv => kv.Key))
            w.Row($"CH{ch}", c.C0, c.C1, c.Tc);
    }

    private static void WritePoints(CsvReportWriter w, string title, IReadOnlyList<PointMeasurement> points)
    {
        if (points.Count == 0) return;
        w.Section(title);
        var header = new List<object?> { "nr", "czas", "nastawa_V", "wzorzec_V", "wzorzec_sigma_V", "wzorzec_n", "temp_C", "proby" };
        for (var ch = 0; ch < AfeModel.ChannelCount; ch++) header.AddRange([$"CH{ch}_V", $"CH{ch}_sigma_V", $"CH{ch}_n"]);
        w.Row([.. header]);
        foreach (var p in points)
        {
            var row = new List<object?> { p.Index + 1, p.Time, p.Setpoint, p.Reference.Mean, p.Reference.StdDev, p.Reference.Count, p.BoardTemperature, p.Attempts };
            foreach (var s in p.Channels)
                row.AddRange(s is { } v ? [v.Mean, v.StdDev, v.Count] : [null, null, null]);
            w.Row([.. row]);
        }
        w.Flush();
    }
}
