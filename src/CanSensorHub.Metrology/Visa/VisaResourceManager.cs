namespace CanSensorHub.Metrology.Visa;

/// <summary>
/// Domyślny menedżer zasobów VISA: wyszukiwanie adresów przyrządów i otwieranie sesji.
/// Brak zainstalowanej biblioteki VISA nie jest błędem programu — <see cref="TryCreate"/>
/// zwraca wtedy <c>null</c> i opis przyczyny, a narzędzie może pracować na przyrządach
/// symulowanych.
/// </summary>
public sealed class VisaResourceManager : IDisposable
{
    private uint _rm;

    private VisaResourceManager(uint rm) => _rm = rm;

    public static VisaResourceManager? TryCreate(out string? error)
    {
        try
        {
            var status = VisaNative.OpenDefaultRM(out var rm);
            if (status < 0)
            {
                error = $"Nie można otworzyć menedżera zasobów VISA: {VisaNative.Describe(0, status)}.";
                return null;
            }
            error = null;
            return new VisaResourceManager(rm);
        }
        catch (DllNotFoundException)
        {
            error = "Nie znaleziono biblioteki VISA (visa64.dll). Wymagana jest instalacja NI-VISA albo Keysight IO Libraries.";
            return null;
        }
        catch (Exception ex)
        {
            error = $"Błąd inicjalizacji VISA: {ex.Message}";
            return null;
        }
    }

    /// <summary>
    /// Adresy przyrządów GPIB, USBTMC i LAN; porty szeregowe tylko na wyraźne żądanie. Porty ASRL
    /// są pomijane domyślnie, bo należą do nich także adaptery innych urządzeń — m.in. adapter CAN
    /// tej aplikacji — a zapytanie <c>*IDN?</c> wysłane na taki port zakłóca jego protokół.
    /// </summary>
    public IReadOnlyList<string> FindInstruments(bool includeSerial = false)
    {
        string[] expressions = includeSerial
            ? ["GPIB?*INSTR", "USB?*INSTR", "TCPIP?*INSTR", "ASRL?*INSTR"]
            : ["GPIB?*INSTR", "USB?*INSTR", "TCPIP?*INSTR"];
        var result = new List<string>();
        foreach (var expression in expressions)
        {
            try { result.AddRange(FindResources(expression)); }
            catch (InstrumentException) { /* interfejs nieobsługiwany przez daną implementację VISA */ }
        }
        return result.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Adresy zasobów VISA pasujące do wyrażenia (składnia <c>viFindRsrc</c>).</summary>
    public IReadOnlyList<string> FindResources(string expression)
    {
        ObjectDisposedException.ThrowIf(_rm == 0, this);
        var result = new List<string>();
        var buf = VisaNative.DescriptionBuffer();
        var status = VisaNative.FindRsrc(_rm, expression, out var list, out var count, buf);
        if (status == VisaNative.StatusResourceNotFound || count == 0) return result;
        if (status < 0) throw new InstrumentException($"Wyszukiwanie zasobów VISA nie powiodło się: {VisaNative.Describe(_rm, status)}.");
        try
        {
            result.Add(VisaNative.FromBuffer(buf));
            for (var i = 1u; i < count; i++)
            {
                if (VisaNative.FindNext(list, buf) < 0) break;
                result.Add(VisaNative.FromBuffer(buf));
            }
        }
        finally
        {
            VisaNative.Close(list);
        }
        return result.Where(r => r.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToList();
    }

    public IScpiSession Open(string resourceName, TimeSpan timeout)
    {
        ObjectDisposedException.ThrowIf(_rm == 0, this);
        var status = VisaNative.Open(_rm, resourceName, 0, (uint)timeout.TotalMilliseconds, out var vi);
        if (status < 0) throw new InstrumentException($"Nie można otworzyć {resourceName}: {VisaNative.Describe(_rm, status)}.");
        return new VisaSession(resourceName, vi, timeout);
    }

    public void Dispose()
    {
        if (_rm == 0) return;
        VisaNative.Close(_rm);
        _rm = 0;
    }
}
