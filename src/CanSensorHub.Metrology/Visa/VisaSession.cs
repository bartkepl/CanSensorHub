using System.Text;

namespace CanSensorHub.Metrology.Visa;

/// <summary>
/// Sesja VISA z jednym przyrządem. Wywołania biblioteki blokują wątek na czas całej operacji
/// wejścia-wyjścia (do limitu czasu), więc każda operacja jest przenoszona do puli wątków —
/// wątek interfejsu nie zamarza na czas pomiaru multimetru.
/// </summary>
internal sealed class VisaSession : IScpiSession
{
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly TimeSpan _defaultTimeout;
    private uint _vi;

    public string ResourceName { get; }

    public VisaSession(string resourceName, uint vi, TimeSpan defaultTimeout)
    {
        ResourceName = resourceName;
        _vi = vi;
        _defaultTimeout = defaultTimeout;
    }

    public async Task WriteAsync(string command, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try { await Task.Run(() => WriteCore(command), ct).ConfigureAwait(false); }
        finally { _lock.Release(); }
    }

    public async Task<string> QueryAsync(string command, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await Task.Run(() =>
            {
                WriteCore(command);
                return ReadCore(timeout ?? _defaultTimeout);
            }, ct).ConfigureAwait(false);
        }
        finally { _lock.Release(); }
    }

    private void WriteCore(string command)
    {
        ObjectDisposedException.ThrowIf(_vi == 0, this);
        var bytes = Encoding.ASCII.GetBytes(command + "\n");
        var status = VisaNative.Write(_vi, bytes, (uint)bytes.Length, out _);
        if (status < 0)
            throw new InstrumentException($"{ResourceName}: zapis „{command}” nie powiódł się — {VisaNative.Describe(_vi, status)}.");
    }

    private string ReadCore(TimeSpan timeout)
    {
        VisaNative.SetAttribute(_vi, VisaNative.AttrTimeoutMs, (nuint)Math.Max(1, (long)timeout.TotalMilliseconds));
        var result = new StringBuilder();
        var buf = new byte[4096];
        while (true)
        {
            var status = VisaNative.Read(_vi, buf, (uint)buf.Length, out var count);
            if (status < 0)
            {
                if (status == VisaNative.StatusTimeout)
                {
                    // Po przekroczeniu czasu przyrząd może wciąż trzymać odpowiedź w buforze wyjściowym;
                    // bez wyczyszczenia następne zapytanie odczytałoby odpowiedź na poprzednie.
                    VisaNative.Clear(_vi);
                    throw new TimeoutException($"{ResourceName}: brak odpowiedzi w ciągu {timeout.TotalSeconds:0.#} s.");
                }
                throw new InstrumentException($"{ResourceName}: odczyt nie powiódł się — {VisaNative.Describe(_vi, status)}.");
            }
            result.Append(Encoding.ASCII.GetString(buf, 0, (int)count));
            // Status dodatni VI_SUCCESS_MAX_CNT (0x3FFF0006) oznacza zapełniony bufor bez końca komunikatu.
            if (status != 0x3FFF0006) break;
        }
        return result.ToString().TrimEnd('\r', '\n');
    }

    public void Dispose()
    {
        if (_vi == 0) return;
        VisaNative.Close(_vi);
        _vi = 0;
        _lock.Dispose();
    }
}
