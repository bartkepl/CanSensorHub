using CanSensorHub.Metrology.Analysis;
using CanSensorHub.Metrology.Instruments;
using CanSensorHub.Tools.MpccAfeCal.Node;

namespace CanSensorHub.Tools.MpccAfeCal.Calibration;

public enum ProcedureMessageKind { Info, Warning, Error }

public sealed record ProcedureMessage(ProcedureMessageKind Kind, string Text);

/// <summary>Postęp procedury: komunikat, ewentualnie ukończony punkt i ułamek wykonania.</summary>
public sealed record ProcedureProgress(ProcedureMessage? Message, PointMeasurement? Point, double Fraction);

/// <summary>
/// Pomiar serii punktów: nastawa zadajnika, ustalenie, równoległa seria wzorca i seria odczytów
/// węzła, kontrola stabilności wzorca. Po zakończeniu — także po anulowaniu i po błędzie —
/// zadajnik wraca do 0 V.
/// </summary>
public sealed class AfeCalibrationProcedure(
    IVoltageSource source,
    IVoltmeter reference,
    MpccNodeLink node,
    AfeCalSettings settings,
    TimeSpan nodeRoundInterval,
    TimeSpan settleTime)
{
    /// <summary>Ile razy ponowić odczyt kanału zgłaszającego NOT_READY, zanim runda go pominie.</summary>
    private const int NotReadyRetries = 3;

    /// <summary>
    /// Odstępy wynikające z okresu pomiaru węzła. Kolejne rundy odczytu muszą być rozdzielone co
    /// najmniej okresem pomiaru — inaczej narzędzie wielokrotnie odczyta tę samą próbkę i zaniży
    /// rozrzut. Ustalanie obejmuje dwa okresy: próbka w toku może pochodzić sprzed zmiany nastawy.
    /// </summary>
    public static (TimeSpan RoundInterval, TimeSpan Settle) Timing(AfeCalSettings s, double measurePeriodMs) =>
        (TimeSpan.FromMilliseconds(s.NodeRoundIntervalMs > 0 ? s.NodeRoundIntervalMs : measurePeriodMs),
         TimeSpan.FromMilliseconds(s.SettleMs > 0 ? s.SettleMs : 2 * measurePeriodMs + 500));

    public async Task<IReadOnlyList<PointMeasurement>> MeasureAsync(
        IReadOnlyList<double> setpoints, IProgress<ProcedureProgress>? progress, CancellationToken ct)
    {
        void Report(ProcedureMessageKind kind, string text, double fraction, PointMeasurement? point = null) =>
            progress?.Report(new ProcedureProgress(new ProcedureMessage(kind, text), point, fraction));

        var results = new List<PointMeasurement>();
        try
        {
            await reference.ConfigureAsync(ct).ConfigureAwait(false);
            for (var i = 0; i < setpoints.Count; i++)
            {
                var sp = setpoints[i];
                var fraction = (double)i / setpoints.Count;
                PointMeasurement? point = null;
                for (var attempt = 1; attempt <= settings.MaxAttempts; attempt++)
                {
                    Report(ProcedureMessageKind.Info, $"Punkt {i + 1}/{setpoints.Count}: {sp:0.000} V" + (attempt > 1 ? $" (próba {attempt})" : ""), fraction);
                    await source.SetVoltageAsync(sp, ct).ConfigureAwait(false);
                    await Task.Delay(settleTime, ct).ConfigureAwait(false);

                    var refTask = reference.ReadSamplesAsync(settings.DmmSamples, ct);
                    var nodeTask = ReadNodeRoundsAsync(ct);
                    await Task.WhenAll(refTask, nodeTask).ConfigureAwait(false);
                    var temperature = await node.ReadBoardTemperatureAsync(ct).ConfigureAwait(false);

                    var refStats = SampleStats.From(refTask.Result);
                    point = new PointMeasurement(i, sp, refStats, nodeTask.Result, temperature, DateTimeOffset.Now, attempt);
                    if (refStats.StdDev * 1000.0 <= settings.MaxReferenceStdDevMv) break;

                    Report(ProcedureMessageKind.Warning,
                        $"Punkt {sp:0.000} V: rozrzut wzorca σ = {refStats.StdDev * 1000.0:0.000} mV > {settings.MaxReferenceStdDevMv:0.###} mV" +
                        (attempt < settings.MaxAttempts ? " — powtórzenie." : " — przyjęty mimo przekroczenia."), fraction);
                }
                results.Add(point!);
                Report(ProcedureMessageKind.Info, $"Punkt {sp:0.000} V: wzorzec {point!.Reference.Mean:0.000000} V", (double)(i + 1) / setpoints.Count, point);
            }
        }
        finally
        {
            try
            {
                await source.SetZeroAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Report(ProcedureMessageKind.Error, $"Nie udało się ustawić zadajnika na 0 V: {ex.Message}. Wyłączyć wyjście ręcznie.", 1.0);
            }
        }
        return results;
    }

    /// <summary>Rundy odczytu wszystkich wybranych kanałów; wynik w woltach, <c>null</c> dla kanału bez ważnych próbek.</summary>
    private async Task<IReadOnlyList<SampleStats?>> ReadNodeRoundsAsync(CancellationToken ct)
    {
        var channels = settings.EnabledChannelIndices;
        var samples = Enumerable.Range(0, AfeModel.ChannelCount).Select(_ => new List<double>()).ToArray();
        for (var round = 0; round < settings.NodeRounds; round++)
        {
            if (round > 0) await Task.Delay(nodeRoundInterval, ct).ConfigureAwait(false);
            foreach (var ch in channels)
            {
                for (var retry = 0; retry <= NotReadyRetries; retry++)
                {
                    var mv = await node.ReadAdcMillivoltsAsync(ch, ct).ConfigureAwait(false);
                    if (mv is { } v) { samples[ch].Add(v * 0.001); break; }
                    await Task.Delay(50, ct).ConfigureAwait(false);
                }
            }
        }
        return samples.Select(s => s.Count > 0 ? SampleStats.From(s) : (SampleStats?)null).ToList();
    }
}
