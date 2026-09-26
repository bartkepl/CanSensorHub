using System.Reflection;
using CanSensorHub.Metrology.Reporting;
using CanSensorHub.Tools.MpccAfeCal.Calibration;
using static CanSensorHub.Metrology.Reporting.HtmlReportBuilder;

namespace CanSensorHub.Tools.MpccAfeCal.Reporting;

/// <summary>
/// Raport sesji w postaci dokumentu HTML do odczytu i druku (oraz konwersji do PDF). Zawiera te same
/// dane co raport CSV, uporządkowane dla człowieka: wynik na początku, dane surowe na końcu.
/// </summary>
public static class AfeHtmlReport
{
    // Te same kolory kanałów co w oknie narzędzia i na wykresach modułów (Tableau 10).
    private static readonly string[] ChannelColors = ["#4E79A7", "#F28E2B", "#E15759", "#76B7B2", "#59A14F", "#EDC948", "#B07AA1"];

    public static string Build(AfeCalibrationSession s)
    {
        var r = new HtmlReportBuilder($"Kalibracja wejść napięciowych MPCC — {s.NodeLabel}");
        var ver = typeof(AfeHtmlReport).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        var channels = s.Settings.EnabledChannelIndices;

        r.Paragraph($"Sesja rozpoczęta {s.Started:yyyy-MM-dd HH:mm:ss} · operacja: {s.Operation} · tryb: {(s.Simulated ? "symulacja" : "stanowisko")}", "muted");

        // --- wynik ---
        r.Section("Wynik");
        var verdict = s.Checks.Count == 0 ? ("none", "BEZ SPRAWDZENIA")
            : s.Checks.All(c => c.Pass) ? ("pass", "PASS — wszystkie kanały w tolerancji")
            : ("fail", "FAIL — kanały poza tolerancją: " + string.Join(", ", s.Checks.Where(c => !c.Pass).Select(c => $"CH{c.Channel}")));
        r.Raw($"<p><span class=\"verdict {verdict.Item1}\">{Encode(verdict.Item2)}</span></p>");
        if (s.Write is { } w)
            r.Paragraph(w.Success ? "Współczynniki zapisane do węzła (SAVE_CONFIG), blokada CAL_LOCK założona." : "Zapis do węzła nie powiódł się — węzeł pozostał przy poprzednich współczynnikach.");
        else if (s.Calibrations.Count > 0)
            r.Paragraph("Współczynniki wyznaczone, nie zapisane do węzła.", "muted");

        r.Table(
            ["Kanał", "c0 przed [V]", "c1 przed", "c0 po [V]", "c1 po", "a", "b [mV]", "Resid. [mV]", "Kalibracja", "Maks. błąd [mV]", "Sprawdzenie"],
            channels.Select(ch => ChannelRow(s, ch)).ToList());
        r.EndSection();

        // --- wykres ---
        var chart = new SvgLineChart { XLabel = "Napięcie wzorca [V]", YLabel = "Błąd węzła [mV]" };
        if (s.Checks.Count > 0 || s.CalibrationPoints.Count > 0)
        {
            chart.ReferenceLines.AddRange([s.Settings.ToleranceMv, -s.Settings.ToleranceMv]);
            foreach (var ch in channels)
                chart.Series.Add(new SvgSeries($"CH{ch} przed", ChannelColors[ch], ErrorPoints(s.CalibrationPoints, ch), Dashed: true, StrokeWidth: 1.2));
            foreach (var ch in channels)
                chart.Series.Add(new SvgSeries($"CH{ch} sprawdzenie", ChannelColors[ch], ErrorPoints(s.CheckPoints, ch), StrokeWidth: 2.2));
            r.Section("Błąd węzła względem wzorca");
            r.Raw(chart.Render());
            r.Paragraph($"Linie przerywane — odczyty z punktów kalibracji, przy współczynnikach sprzed zapisu. Linie ciągłe — sprawdzenie. Linie kropkowane — tolerancja ±{Num(s.Settings.ToleranceMv, 1)} mV.", "muted");
            r.EndSection();
        }

        // --- kontekst ---
        r.Section("Węzeł i przyrządy");
        r.KeyValues(
        [
            ("Węzeł", $"NODE 0x{s.NodeId:X2}"),
            ("UID", s.Node?.UidHex),
            ("Firmware", s.Node is { } n ? $"{n.FwMajor}.{n.FwMinor} build {n.BuildRevision}" : null),
            ("Sprzęt", s.Node is { } h ? $"{h.HwMajor}.{h.HwMinor}" : null),
            ("Wzorzec", s.ReferenceInstrument),
            ("Zadajnik", s.SourceInstrument),
            ("Tref", $"{Num(s.Tref, 1)} °C"),
            ("MEASURE_PERIOD", $"{s.MeasurePeriodMs:0} ms"),
            ("Narzędzie", $"CanSensorHub.Tools.MpccAfeCal {ver}"),
        ]);
        r.EndSection();

        var st = s.Settings;
        r.Section("Ustawienia przebiegu");
        r.KeyValues(
        [
            ("Zakres", $"{Num(st.MinVoltage, 3)} … {Num(st.MaxVoltage, 3)} V"),
            ("Kalibracja", st.Mode == CalibrationMode.TwoPoint ? "dwupunktowa" : $"wielopunktowa, {st.CalibrationPoints} pkt"),
            ("Sprawdzenie", $"{st.CheckPoints} pkt"),
            ("Kanały", string.Join(", ", channels.Select(c => $"CH{c}"))),
            ("Wzorzec", $"{st.DmmSamples} próbek, NPLC {Num(st.DmmNplc, 2)}, autozero {(st.DmmAutoZero ? "wł." : "wył.")}"),
            ("Węzeł", $"{st.NodeRounds} rund odczytu"),
            ("Tolerancja", $"±{Num(st.ToleranceMv, 2)} mV"),
        ]);
        r.EndSection();

        if (s.Write is { } wr)
        {
            r.Section("Zapis do węzła");
            r.Table(["Krok"], wr.Log.Select(l => (IReadOnlyList<HtmlCell>)[new HtmlCell(l)]).ToList(), "compact");
            r.EndSection();
        }

        PointsSection(r, "Punkty kalibracji", s.CalibrationPoints, channels);
        PointsSection(r, "Punkty sprawdzenia", s.CheckPoints, channels);
        return r.Build();
    }

    private static IReadOnlyList<HtmlCell> ChannelRow(AfeCalibrationSession s, int ch)
    {
        s.CoefficientsBefore.TryGetValue(ch, out var before);
        var cal = s.Calibrations.FirstOrDefault(c => c.Channel == ch);
        var change = s.Changes.FirstOrDefault(c => c.Channel == ch);
        var after = change?.After ?? cal?.After;
        var check = s.Checks.FirstOrDefault(c => c.Channel == ch);
        var calText = cal?.Rejection ?? (change is not null ? (s.Write?.Success == true ? "zapisano" : "nie zapisano") : cal is not null ? "wyznaczono" : "—");
        return
        [
            $"CH{ch}",
            new(Num(before.C0, 6, sign: true), "num"), new(Num(before.C1, 6), "num"),
            new(Num(after?.C0, 6, sign: true), "num"), new(Num(after?.C1, 6), "num"),
            new(Num(cal?.Fit.Slope, 6), "num"), new(Num(cal?.Fit.Intercept * 1000.0, 2, sign: true), "num"),
            new(Num(cal?.Fit.MaxAbsResidual * 1000.0, 2), "num"),
            new(calText, cal is { Accepted: false } ? "warn" : null),
            new(Num(check is { Points.Count: > 0 } ? check.MaxAbsErrorMv : null, 2), "num"),
            check is null || check.Points.Count == 0 ? new HtmlCell("—") : new HtmlCell(check.Pass ? "PASS" : "FAIL", check.Pass ? "pass" : "fail"),
        ];
    }

    private static IReadOnlyList<(double, double)> ErrorPoints(IReadOnlyList<PointMeasurement> pts, int ch) =>
        pts.Where(p => p.Channels[ch] is not null)
           .Select(p => (p.Reference.Mean, (p.Channels[ch]!.Value.Mean - p.Reference.Mean) * 1000.0))
           .ToList();

    private static void PointsSection(HtmlReportBuilder r, string title, IReadOnlyList<PointMeasurement> pts, IReadOnlyList<int> channels)
    {
        if (pts.Count == 0) return;
        r.Section(title);
        var headers = new List<string> { "Nr", "Nastawa [V]", "Wzorzec [V]", "σ wz. [mV]", "T [°C]" };
        headers.AddRange(channels.Select(c => $"CH{c} Δ [mV]"));
        r.Table(headers, pts.Select(p =>
        {
            var row = new List<HtmlCell>
            {
                new($"{p.Index + 1}", "num"), new(Num(p.Setpoint, 3), "num"), new(Num(p.Reference.Mean, 6), "num"),
                new(Num(p.Reference.StdDev * 1000.0, 3), "num"), new(Num(p.BoardTemperature, 2), "num"),
            };
            row.AddRange(channels.Select(c => new HtmlCell(
                Num(p.Channels[c] is { } v ? (v.Mean - p.Reference.Mean) * 1000.0 : null, 2, sign: true), "num")));
            return (IReadOnlyList<HtmlCell>)row;
        }).ToList(), "compact");
        r.Paragraph("Δ — średnia odczytów węzła minus średnia wzorca. Dane surowe (σ i liczność serii każdego kanału) w raporcie CSV.", "muted");
        r.EndSection();
    }
}
