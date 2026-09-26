namespace CanSensorHub.Metrology.Visa;

/// <summary>
/// Sesja tekstowa SCPI z jednym przyrządem. Implementacje serializują dostęp: zapis jednej
/// komendy nie może przepleść się z odczytem odpowiedzi innej.
/// </summary>
public interface IScpiSession : IDisposable
{
    string ResourceName { get; }

    Task WriteAsync(string command, CancellationToken ct = default);

    /// <summary>Wysyła komendę i czyta jedną odpowiedź. <paramref name="timeout"/> nadpisuje domyślny limit sesji dla tego odczytu.</summary>
    Task<string> QueryAsync(string command, TimeSpan? timeout = null, CancellationToken ct = default);
}

/// <summary>Błąd zgłoszony przez przyrząd w kolejce <c>SYST:ERR?</c> albo przez warstwę VISA.</summary>
public sealed class InstrumentException(string message) : Exception(message);
