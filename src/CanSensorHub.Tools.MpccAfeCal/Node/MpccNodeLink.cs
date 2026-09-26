using CanSensorHub.Core.Can;
using CanSensorHub.Core.Protocol;
using CanSensorHub.Modules.Mpcc.Protocol;

namespace CanSensorHub.Tools.MpccAfeCal.Node;

/// <summary>Węzeł odrzucił żądanie albo nie odpowiedział.</summary>
public sealed class NodeRequestException(string message, StatusCode? status = null) : Exception(message)
{
    public StatusCode? Status { get; } = status;
}

/// <summary>
/// Żądanie–odpowiedź z jednym węzłem MPCC, z oczekiwaniem na wynik. Moduł urządzenia komunikuje
/// się zdarzeniami (odpowiedź trafia do widoku, gdy nadejdzie), a procedura kalibracji potrzebuje
/// wyniku każdego kroku, zanim wykona następny — stąd osobna, sekwencyjna warstwa.
/// </summary>
/// <remarks>
/// Odpowiedzi są odbierane przez <see cref="CanBusService.RawFrameReceived"/>, na wątku transportu,
/// a nie przez kolejkę wątku interfejsu: przy obciążonym dzienniku ramek kolejka potrafi opóźnić
/// odpowiedź poza limit czasu, mimo że dotarła na czas. W danej chwili oczekuje najwyżej jedno żądanie.
/// </remarks>
public sealed class MpccNodeLink : IDisposable
{
    private readonly CanBusService _bus;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Lock _pendingLock = new();
    private readonly StreamAssembler _asm = new();
    private Pending? _pending;

    public byte NodeId { get; }
    public TimeSpan Timeout { get; init; } = TimeSpan.FromMilliseconds(500);
    public int Retries { get; init; } = 2;

    private sealed class Pending(byte op, byte arg, bool expectsStream)
    {
        public byte Op { get; } = op;
        public byte Arg { get; } = arg;
        public bool ExpectsStream { get; } = expectsStream;
        public TaskCompletionSource<(ResponseMessage? Response, byte[]? Stream)> Done { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public MpccNodeLink(CanBusService bus, byte nodeId)
    {
        _bus = bus;
        NodeId = nodeId;
        _bus.RawFrameReceived += OnRawFrame;
    }

    private void OnRawFrame(object? sender, CanFrame frame)
    {
        var id = CanId.Unpack(frame.Id);
        if (id.Node != NodeId) return;

        Pending? p;
        lock (_pendingLock) p = _pending;

        switch ((CanFunc)id.Func)
        {
            case CanFunc.Response:
                if (p is not null && id.Obj == p.Op && id.Arg == p.Arg)
                {
                    var r = EnvelopeCodec.ParseResponse(id, frame.Data);
                    // Żądanie strumieniowe kończy się odpowiedzią jednoramkową tylko przy błędzie.
                    if (!p.ExpectsStream || r.Status != StatusCode.Ok) p.Done.TrySetResult((r, null));
                }
                break;
            case CanFunc.Stream:
                byte[]? data;
                lock (_asm) data = _asm.Push(EnvelopeCodec.ParseStream(id, frame.Data));
                if (data is not null && p is { ExpectsStream: true } && id.Obj == p.Op && id.Arg == p.Arg)
                    p.Done.TrySetResult((null, data));
                break;
        }
    }

    private async Task<(ResponseMessage? Response, byte[]? Stream)> ExchangeAsync(
        MpccReqOp op, byte arg, byte[] data, bool expectsStream, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            for (var attempt = 0; ; attempt++)
            {
                var p = new Pending((byte)op, arg, expectsStream);
                lock (_pendingLock) _pending = p;
                try
                {
                    await _bus.SendAsync(EnvelopeCodec.BuildRequest(NodeId, (byte)op, arg, data), ct).ConfigureAwait(false);
                    return await p.Done.Task.WaitAsync(Timeout, ct).ConfigureAwait(false);
                }
                catch (TimeoutException) when (attempt < Retries)
                {
                    // Wszystkie żądania tej warstwy są idempotentne, więc powtórzenie jest bezpieczne.
                }
                catch (TimeoutException)
                {
                    throw new NodeRequestException($"Węzeł 0x{NodeId:X2} nie odpowiedział na {op} (ARG=0x{arg:X2}) po {Retries + 1} próbach.");
                }
                finally
                {
                    lock (_pendingLock) if (ReferenceEquals(_pending, p)) _pending = null;
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<ResponseMessage> RequestAsync(MpccReqOp op, byte arg = 0, byte[]? data = null, CancellationToken ct = default) =>
        (await ExchangeAsync(op, arg, data ?? [], expectsStream: false, ct).ConfigureAwait(false)).Response!.Value;

    public async Task<byte[]> RequestStreamAsync(MpccReqOp op, byte arg = 0, CancellationToken ct = default)
    {
        var (response, stream) = await ExchangeAsync(op, arg, [], expectsStream: true, ct).ConfigureAwait(false);
        if (stream is not null) return stream;
        throw new NodeRequestException($"Węzeł 0x{NodeId:X2} odrzucił {op}: {response!.Value.Status}.", response.Value.Status);
    }

    private static void EnsureOk(ResponseMessage r, string what)
    {
        if (r.Status != StatusCode.Ok)
            throw new NodeRequestException($"{what}: węzeł zwrócił {r.Status}.", r.Status);
    }

    // --- operacje typowane -----------------------------------------------------------------------

    public async Task<double> ReadParamAsync(ParamDescriptor p, CancellationToken ct = default)
    {
        var r = await RequestAsync(MpccReqOp.ReadParam, p.Id, ct: ct).ConfigureAwait(false);
        EnsureOk(r, $"Odczyt {p.Name}");
        if (r.Data.Length < ParamCodec.ByteSize(p.Type))
            throw new NodeRequestException($"Odczyt {p.Name}: odpowiedź krótsza niż typ parametru.");
        return ParamCodec.Decode(p.Type, r.Data);
    }

    /// <summary>Zapis parametru; zwraca status węzła bez zgłaszania wyjątku, bo odmowa (np. <c>ERR_READONLY</c>) bywa oczekiwana.</summary>
    public async Task<StatusCode> WriteParamAsync(ParamDescriptor p, double value, CancellationToken ct = default) =>
        (await RequestAsync(MpccReqOp.WriteParam, p.Id, ParamCodec.Encode(p.Type, value), ct).ConfigureAwait(false)).Status;

    public async Task SaveConfigAsync(CancellationToken ct = default) =>
        EnsureOk(await RequestAsync(MpccReqOp.SaveConfig, ct: ct).ConfigureAwait(false), "SAVE_CONFIG");

    /// <summary>Napięcie wejścia AFE w mV po przeliczeniu przez współczynniki węzła; <c>null</c>, gdy kanał nie ma jeszcze ważnej próbki.</summary>
    public async Task<int?> ReadAdcMillivoltsAsync(int channel, CancellationToken ct = default)
    {
        var r = await RequestAsync(MpccReqOp.ReadAdc, (byte)channel, ct: ct).ConfigureAwait(false);
        if (r.Status == StatusCode.ErrNotReady) return null;
        EnsureOk(r, $"READ_ADC kanał {channel}");
        if (r.Data.Length < 4) throw new NodeRequestException($"READ_ADC kanał {channel}: odpowiedź bez wartości.");
        return BitConverter.ToInt32(r.Data, 0);
    }

    public async Task<DeviceIdentity> GetInfoAsync(CancellationToken ct = default) =>
        DeviceIdentity.Parse(await RequestStreamAsync(MpccReqOp.GetInfo, ct: ct).ConfigureAwait(false));

    /// <summary>Temperatura płytki z STS31 — ta sama, której firmware używa w kompensacji temperaturowej AFE.</summary>
    public async Task<double?> ReadBoardTemperatureAsync(CancellationToken ct = default)
    {
        byte[] data;
        try
        {
            data = await RequestStreamAsync(MpccReqOp.ReadSensor, (byte)MpccSensor.Sts31Cpu, ct).ConfigureAwait(false);
        }
        catch (NodeRequestException ex) when (ex.Status is StatusCode.ErrSensorAbsent or StatusCode.ErrSensorFault or StatusCode.ErrNotReady)
        {
            return null;
        }
        // Rekord sensor_reading: [QTY u8, VALUE i32 LE, READING_STATUS u8].
        for (var i = 0; i + 6 <= data.Length; i += 6)
        {
            if (data[i] == (byte)MpccQuantity.Temp)
                return BitConverter.ToInt32(data, i + 1) * MpccTables.Quantities[MpccQuantity.Temp].Scale;
        }
        return null;
    }

    public void Dispose()
    {
        _bus.RawFrameReceived -= OnRawFrame;
        _gate.Dispose();
    }
}
