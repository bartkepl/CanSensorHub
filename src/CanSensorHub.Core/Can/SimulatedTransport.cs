using System.Collections.Concurrent;

namespace CanSensorHub.Core.Can;

/// <summary>
/// In-process CAN bus with no hardware involved. Used for the "Symulator" connection mode: any number
/// of <see cref="SimulatedTransport"/> endpoints (the app's own client, plus one fake-device endpoint per
/// simulated module instance) can be attached and will see each other's frames, exactly like a real bus.
/// </summary>
public sealed class SimulatedCanBus
{
    private readonly ConcurrentDictionary<SimulatedTransport, byte> _endpoints = new();

    public SimulatedTransport CreateEndpoint(string name) => new(this, name);

    internal void Attach(SimulatedTransport ep) => _endpoints.TryAdd(ep, 0);
    internal void Detach(SimulatedTransport ep) => _endpoints.TryRemove(ep, out _);

    internal void Publish(SimulatedTransport sender, CanFrame frame)
    {
        foreach (var ep in _endpoints.Keys)
        {
            if (ReferenceEquals(ep, sender)) continue;
            ep.Deliver(frame);
        }
    }
}

public sealed class SimulatedTransport : ICanTransport
{
    private readonly SimulatedCanBus _bus;
    private volatile bool _disposed;

    public string Description { get; }
    public event EventHandler<CanFrameReceivedEventArgs>? FrameReceived;

    internal SimulatedTransport(SimulatedCanBus bus, string name)
    {
        _bus = bus;
        Description = name;
        _bus.Attach(this);
    }

    internal void Deliver(CanFrame frame)
    {
        if (_disposed) return;
        FrameReceived?.Invoke(this, new CanFrameReceivedEventArgs(frame));
    }

    public Task SendAsync(CanFrame frame, CancellationToken ct = default)
    {
        if (!_disposed) _bus.Publish(this, frame);
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _bus.Detach(this);
    }
}
