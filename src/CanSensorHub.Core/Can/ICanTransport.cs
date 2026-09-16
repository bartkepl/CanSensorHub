namespace CanSensorHub.Core.Can;

/// <summary>Direction a frame travelled, for logging/UI purposes.</summary>
public enum FrameDirection
{
    Rx,
    Tx,
}

public sealed class CanFrameReceivedEventArgs(CanFrame frame) : EventArgs
{
    public CanFrame Frame { get; } = frame;
}

/// <summary>
/// Backend-agnostic CAN bus connection. Implementations: <see cref="SlcanTransport"/> (real WeAct
/// USB2CANFDV1 adapter over a slcan virtual COM port) and <see cref="SimulatedTransport"/> (in-process,
/// no hardware required).
/// </summary>
public interface ICanTransport : IDisposable
{
    /// <summary>Human-readable description of the connection (port name, bitrate, ...).</summary>
    string Description { get; }

    event EventHandler<CanFrameReceivedEventArgs>? FrameReceived;

    Task SendAsync(CanFrame frame, CancellationToken ct = default);
}
