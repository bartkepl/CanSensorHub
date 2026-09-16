using CanSensorHub.Core.Can;
using CanSensorHub.Core.Protocol;

namespace CanSensorHub.Core.Bootloader;

/// <summary>
/// Bootloader link over the shell's shared CAN connection. Host→BL uses FUNC=REQUEST, BL→host replies
/// FUNC=RESPONSE with byte0=status; both addressed with the bootloader's fixed node id (0x10 for
/// MPCC_BL, 0x01 for MPSWP_BL — hardcoded in the bootloader image, independent of any NODE_ID the
/// application firmware was reconfigured to use).
/// </summary>
public sealed class BlCanLink : IBlLink
{
    private readonly CanBusService _bus;
    private readonly byte _blNodeId;
    private readonly object _pendingLock = new();
    private (byte Opcode, TaskCompletionSource<byte[]> Tcs)? _pending;

    public BlCanLink(CanBusService bus, byte blNodeId)
    {
        _bus = bus;
        _blNodeId = blNodeId;
        // RawFrameReceived (not FrameReceived): the latter is marshaled through the UI dispatcher queue,
        // which the PROG_DATA burst (64 sends/block, each also posting a bus-monitor log update) can back
        // up past this request's own timeout — see CanBusService.RawFrameReceived for details.
        _bus.RawFrameReceived += OnFrameReceived;
    }

    private void OnFrameReceived(object? sender, CanFrame frame)
    {
        var id = CanId.Unpack(frame.Id);
        if (id.Func != (byte)CanFunc.Response || id.Node != _blNodeId) return;

        lock (_pendingLock)
        {
            if (_pending is not { } p || p.Opcode != id.Obj) return;
            // byte0 = status, rest = payload
            var data = frame.Data;
            var payload = data.Length > 0 ? data[1..] : [];
            var result = new byte[payload.Length + 1];
            result[0] = data.Length > 0 ? data[0] : (byte)0;
            payload.CopyTo(result, 1);
            p.Tcs.TrySetResult(result);
            _pending = null;
        }
    }

    public async Task<byte[]?> RequestAsync(byte opcode, byte arg, ReadOnlyMemory<byte> payload, TimeSpan timeout, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_pendingLock) _pending = (opcode, tcs);

        var frame = EnvelopeCodec.BuildRequest(_blNodeId, opcode, arg, payload.Span);
        await _bus.SendAsync(frame, ct).ConfigureAwait(false);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            return await tcs.Task.WaitAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return null; // plain timeout
        }
        finally
        {
            lock (_pendingLock)
            {
                if (_pending?.Tcs == tcs) _pending = null;
            }
        }
    }

    public Task SendNoResponseAsync(byte opcode, byte arg, ReadOnlyMemory<byte> payload, CancellationToken ct)
    {
        var frame = EnvelopeCodec.BuildRequest(_blNodeId, opcode, arg, payload.Span);
        return _bus.SendAsync(frame, ct);
    }

    public void Dispose() => _bus.RawFrameReceived -= OnFrameReceived;
}
