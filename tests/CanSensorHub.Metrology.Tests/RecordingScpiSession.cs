using CanSensorHub.Metrology.Visa;

namespace CanSensorHub.Metrology.Tests;

/// <summary>
/// Sesja SCPI rejestrująca komendy. Odpowiedzi na zapytania są dobierane po prefiksie komendy;
/// <c>SYST:ERR?</c> bez zdefiniowanej odpowiedzi zwraca brak błędu.
/// </summary>
internal sealed class RecordingScpiSession : IScpiSession
{
    private readonly Dictionary<string, Queue<string>> _responses = new(StringComparer.Ordinal);

    public string ResourceName => "TEST::INSTR";
    public List<string> Commands { get; } = [];
    public List<TimeSpan?> QueryTimeouts { get; } = [];

    public RecordingScpiSession Respond(string commandPrefix, params string[] responses)
    {
        if (!_responses.TryGetValue(commandPrefix, out var q)) _responses[commandPrefix] = q = new Queue<string>();
        foreach (var r in responses) q.Enqueue(r);
        return this;
    }

    public Task WriteAsync(string command, CancellationToken ct = default)
    {
        Commands.Add(command);
        return Task.CompletedTask;
    }

    public Task<string> QueryAsync(string command, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        Commands.Add(command);
        QueryTimeouts.Add(timeout);
        foreach (var (prefix, q) in _responses.OrderByDescending(kv => kv.Key.Length))
        {
            if (command.StartsWith(prefix, StringComparison.Ordinal) && q.Count > 0)
                return Task.FromResult(q.Dequeue());
        }
        if (command == "SYST:ERR?") return Task.FromResult("+0,\"No error\"");
        throw new InvalidOperationException($"Brak zaprogramowanej odpowiedzi na „{command}”.");
    }

    public void Dispose() { }
}
