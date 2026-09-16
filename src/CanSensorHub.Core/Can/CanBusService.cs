using System.Collections.Concurrent;
using System.Threading;

namespace CanSensorHub.Core.Can;

public enum ConnectionKind { None, Simulated, Slcan }

/// <summary>
/// Owns the single active CAN connection for the whole application (either the real WeAct slcan adapter
/// or the in-process simulator) and fans out every received/sent frame to:
///   - the raw bus monitor (<see cref="FrameLogged"/>, module-agnostic), and
///   - every connected device module's own decoder (<see cref="FrameReceived"/>), which filters by NODE.
/// Only one connection can be active at a time — this mirrors the single WeAct adapter the app manages.
///
/// Frames arrive on backend-owned threads (the slcan adapter's dedicated reader thread, or the
/// simulator's timer pool thread) that have nothing to do with the UI thread. Every subscriber
/// downstream (module view models) ends up mutating WPF-bound ObservableCollections, which WPF requires
/// to happen on the thread that owns them — so this service marshals every public event onto whichever
/// thread constructed it (captured via <see cref="SynchronizationContext.Current"/>, which WPF installs
/// automatically for the UI thread) instead of pushing that requirement onto every module.
/// </summary>
public sealed class CanBusService : IDisposable
{
    public const int MaxLogEntries = 50_000;

    private readonly SynchronizationContext? _uiContext = SynchronizationContext.Current;
    private ICanTransport? _transport;
    private SimulatedCanBus? _simBus;

    public ConnectionKind Kind { get; private set; } = ConnectionKind.None;
    public bool IsConnected => _transport is not null;
    public string? ConnectionDescription => _transport?.Description;

    /// <summary>Exposed so simulated device instances can attach themselves directly to the virtual bus.</summary>
    public SimulatedCanBus? SimulatedBus => _simBus;

    public event EventHandler<CanLogEntry>? FrameLogged;
    public event EventHandler<CanFrame>? FrameReceived;
    public event EventHandler? ConnectionChanged;

    /// <summary>
    /// Same frames as <see cref="FrameReceived"/>, but invoked immediately on the transport's own thread
    /// instead of marshaled through the UI dispatcher queue. <see cref="FrameReceived"/> is fine for
    /// display purposes, but a tight burst of outgoing sends (e.g. the bootloader's 64 PROG_DATA frames
    /// per block) each also posts a <see cref="FrameLogged"/> UI update — under load that backlog can
    /// delay a queued <see cref="FrameReceived"/> callback past a caller's own request timeout even though
    /// the reply physically arrived in time. Time-sensitive request/response matching (bootloader) should
    /// use this instead.
    /// </summary>
    public event EventHandler<CanFrame>? RawFrameReceived;

    private readonly ConcurrentQueue<CanLogEntry> _history = new();
    private int _historyCount;

    public IReadOnlyList<CanLogEntry> HistorySnapshot => _history.ToArray();

    public async Task ConnectSimulatedAsync()
    {
        Disconnect();
        _simBus = new SimulatedCanBus();
        var ep = _simBus.CreateEndpoint("Symulator (bez sprzętu)");
        ep.FrameReceived += OnTransportFrameReceived;
        _transport = ep;
        Kind = ConnectionKind.Simulated;
        RaiseOnUiThread(() => ConnectionChanged?.Invoke(this, EventArgs.Empty));
        await Task.CompletedTask;
    }

    public async Task ConnectSlcanAsync(string portName, int bitrateBps, CancellationToken ct = default)
    {
        Disconnect();
        var t = await SlcanTransport.OpenAsync(portName, bitrateBps, ct).ConfigureAwait(false);
        t.FrameReceived += OnTransportFrameReceived;
        _transport = t;
        Kind = ConnectionKind.Slcan;
        RaiseOnUiThread(() => ConnectionChanged?.Invoke(this, EventArgs.Empty));
    }

    public void Disconnect()
    {
        if (_transport is null) return;
        _transport.FrameReceived -= OnTransportFrameReceived;
        _transport.Dispose();
        _transport = null;
        _simBus = null;
        Kind = ConnectionKind.None;
        RaiseOnUiThread(() => ConnectionChanged?.Invoke(this, EventArgs.Empty));
    }

    public async Task SendAsync(CanFrame frame, CancellationToken ct = default)
    {
        if (_transport is null)
            throw new InvalidOperationException("Brak aktywnego połączenia CAN.");
        await _transport.SendAsync(frame, ct).ConfigureAwait(false);
        Log(frame, FrameDirection.Tx);
    }

    private void OnTransportFrameReceived(object? sender, CanFrameReceivedEventArgs e)
    {
        var frame = e.Frame;
        RawFrameReceived?.Invoke(this, frame);
        Log(frame, FrameDirection.Rx);
        RaiseOnUiThread(() => FrameReceived?.Invoke(this, frame));
    }

    private void Log(CanFrame frame, FrameDirection direction)
    {
        var entry = CanLogEntry.Create(frame, direction);
        _history.Enqueue(entry);
        if (Interlocked.Increment(ref _historyCount) > MaxLogEntries && _history.TryDequeue(out _))
            Interlocked.Decrement(ref _historyCount);
        RaiseOnUiThread(() => FrameLogged?.Invoke(this, entry));
    }

    /// <summary>Runs <paramref name="action"/> on the thread that constructed this service; inline if we're already there.</summary>
    private void RaiseOnUiThread(Action action)
    {
        if (_uiContext is null || SynchronizationContext.Current == _uiContext)
            action();
        else
            _uiContext.Post(_ => action(), null);
    }

    public void ClearHistory()
    {
        while (_history.TryDequeue(out _)) { }
        _historyCount = 0;
    }

    public void Dispose() => Disconnect();
}
