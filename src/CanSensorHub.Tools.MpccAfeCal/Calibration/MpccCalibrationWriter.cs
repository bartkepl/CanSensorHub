using CanSensorHub.Core.Protocol;
using CanSensorHub.Tools.MpccAfeCal.Node;

namespace CanSensorHub.Tools.MpccAfeCal.Calibration;

/// <summary>Wynik zapisu współczynników do węzła.</summary>
public sealed record WriteOutcome(bool Success, IReadOnlyList<string> Log);

/// <summary>
/// Zapis współczynników do węzła w kolejności ustalonej w docs/adr/0005:
/// zdjęcie blokady (RAM) → zapis c1, c0 → odczyt zwrotny → założenie blokady (RAM) → SAVE_CONFIG
/// → kontrola blokady. Jedno SAVE_CONFIG utrwala nowe współczynniki razem z założoną blokadą,
/// więc stan „odblokowany” nigdy nie trafia do pamięci nieulotnej.
/// Przy niepowodzeniu przed SAVE_CONFIG przywracane są poprzednie współczynniki i blokada, bez
/// zapisu do pamięci nieulotnej — węzeł wraca do stanu sprzed operacji.
/// </summary>
public sealed class MpccCalibrationWriter(MpccNodeLink node)
{
    public async Task<WriteOutcome> WriteAsync(IReadOnlyList<ChannelCalibration> calibrations, CancellationToken ct = default)
    {
        var log = new List<string>();
        var accepted = calibrations.Where(c => c.Accepted).ToList();
        if (accepted.Count == 0)
        {
            log.Add("Brak zaakceptowanych kanałów — nic nie zapisano.");
            return new WriteOutcome(false, log);
        }

        var written = new List<ChannelCalibration>();
        var saved = false;
        try
        {
            await ExpectOk(AfeModel.CalLock, 0, "zdjęcie blokady CAL_LOCK", log, ct).ConfigureAwait(false);

            foreach (var c in accepted)
            {
                var info = AfeModel.Channels[c.Channel];
                written.Add(c);
                await ExpectOk(info.C1, c.After.C1, $"{info.Name}: c1 = {c.After.C1:0.000000}", log, ct).ConfigureAwait(false);
                await ExpectOk(info.C0, c.After.C0, $"{info.Name}: c0 = {c.After.C0:+0.000000;-0.000000} V", log, ct).ConfigureAwait(false);
            }

            foreach (var c in accepted)
            {
                var info = AfeModel.Channels[c.Channel];
                await VerifyReadback(info.C1, c.After.C1, ct).ConfigureAwait(false);
                await VerifyReadback(info.C0, c.After.C0, ct).ConfigureAwait(false);
            }
            log.Add("Odczyt zwrotny zgodny z wartościami zapisanymi.");

            await ExpectOk(AfeModel.CalLock, 1, "założenie blokady CAL_LOCK", log, ct).ConfigureAwait(false);
            await node.SaveConfigAsync(ct).ConfigureAwait(false);
            saved = true;
            log.Add("SAVE_CONFIG: współczynniki i blokada zapisane w pamięci nieulotnej.");

            if (await node.ReadParamAsync(AfeModel.CalLock, ct).ConfigureAwait(false) != 1)
                throw new NodeRequestException("Po zapisie CAL_LOCK nie jest ustawiony.");
            return new WriteOutcome(true, log);
        }
        catch (Exception ex) when (!saved)
        {
            log.Add($"Błąd: {ex.Message}");
            await RollbackAsync(written, log).ConfigureAwait(false);
            return new WriteOutcome(false, log);
        }
        catch (Exception ex)
        {
            log.Add($"Błąd po zapisie do pamięci nieulotnej: {ex.Message}. Współczynniki zostały zapisane; stan blokady CAL_LOCK wymaga sprawdzenia.");
            return new WriteOutcome(false, log);
        }
    }

    private async Task ExpectOk(ParamDescriptor p, double value, string what, List<string> log, CancellationToken ct)
    {
        var status = await node.WriteParamAsync(p, value, ct).ConfigureAwait(false);
        if (status != StatusCode.Ok) throw new NodeRequestException($"{what}: węzeł zwrócił {status}.", status);
        log.Add(what);
    }

    private async Task VerifyReadback(ParamDescriptor p, double expected, CancellationToken ct)
    {
        var actual = await node.ReadParamAsync(p, ct).ConfigureAwait(false);
        // Porównanie na poziomie f32: węzeł przechowuje dokładnie tę reprezentację, którą wysłano.
        if ((float)actual != (float)expected)
            throw new NodeRequestException($"Odczyt zwrotny {p.Name}: {actual:R} zamiast {expected:R}.");
    }

    /// <summary>
    /// Przywrócenie poprzednich współczynników i blokady bez SAVE_CONFIG. Anulowanie operacji nie
    /// przerywa wycofania — dlatego bez tokenu anulowania.
    /// </summary>
    private async Task RollbackAsync(IReadOnlyList<ChannelCalibration> written, List<string> log)
    {
        foreach (var c in written)
        {
            var info = AfeModel.Channels[c.Channel];
            try
            {
                await node.WriteParamAsync(info.C1, c.Before.C1).ConfigureAwait(false);
                await node.WriteParamAsync(info.C0, c.Before.C0).ConfigureAwait(false);
                log.Add($"{info.Name}: przywrócono poprzednie współczynniki.");
            }
            catch (Exception ex)
            {
                log.Add($"{info.Name}: nie udało się przywrócić współczynników ({ex.Message}). Pamięć nieulotna nie została zmieniona — reset węzła przywróci poprzedni stan.");
            }
        }
        try
        {
            var status = await node.WriteParamAsync(AfeModel.CalLock, 1).ConfigureAwait(false);
            log.Add(status == StatusCode.Ok ? "Blokada CAL_LOCK przywrócona." : $"Przywrócenie blokady: węzeł zwrócił {status}.");
        }
        catch (Exception ex)
        {
            log.Add($"Nie udało się przywrócić blokady CAL_LOCK ({ex.Message}). Pamięć nieulotna nie została zmieniona — reset węzła przywróci blokadę.");
        }
        log.Add("Pamięć nieulotna węzła bez zmian.");
    }
}
