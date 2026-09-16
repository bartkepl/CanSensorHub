using CanSensorHub.Core.Can;
using CanSensorHub.Core.Logging;
using CanSensorHub.Core.Protocol;
using CanSensorHub.Modules.Mpcc.Protocol;

namespace CanSensorHub.Modules.Mpcc;

public sealed record MpccSensorReading(MpccQuantity Quantity, double Value, string Unit);
public sealed record MpccDeviceInfo(byte ProtocolVersion, byte FwMajor, byte FwMinor, byte Node);
public sealed record MpccRtcTime(int Second, int Minute, int Hour, int Day, int Month, int Year, int WeekDay);

/// <summary>
/// Typed request/response layer for one MPCC node, built on the shared envelope codec. Subscribes to the
/// shell's <see cref="CanBusService"/> and filters by <see cref="NodeId"/>, so multiple devices (MPCC
/// and/or MPSWP instances) can share one physical connection without interfering.
/// </summary>
public sealed class MpccDeviceClient : IDisposable
{
    private readonly CanBusService _bus;
    private readonly StreamAssembler _asm = new();
    private System.Threading.Timer? _pollTimer;
    private WideCsvLogger? _csvLogger;

    public byte NodeId { get; set; }
    public LiveValueStore Values { get; } = new();

    public event EventHandler<TelemetryRaw>? TelemetryReceived;
    public event EventHandler<ResponseMessage>? ResponseReceived;
    public event EventHandler<EventMessage>? EventReceived;
    public event EventHandler<IReadOnlyDictionary<byte, byte>>? SensorsListReceived;
    public event EventHandler<(MpccSensor Sensor, IReadOnlyList<MpccSensorReading> Readings)>? SensorReadingsReceived;
    public event EventHandler<MpccDeviceInfo>? InfoReceived;
    public event EventHandler<DeviceStatusReport>? DeviceStatusReceived;
    public event EventHandler<MpccRtcTime>? TimeReceived;
    public event EventHandler<string>? Error;

    public MpccDeviceClient(CanBusService bus, byte nodeId)
    {
        _bus = bus;
        NodeId = nodeId;
        _bus.FrameReceived += OnFrameReceived;
    }

    private void OnFrameReceived(object? sender, CanFrame frame)
    {
        var id = CanId.Unpack(frame.Id);
        if (id.Node != NodeId) return;

        try
        {
            switch ((CanFunc)id.Func)
            {
                case CanFunc.Telemetry:
                    HandleTelemetry(EnvelopeCodec.ParseTelemetry(id, frame.Data));
                    break;
                case CanFunc.Response:
                    HandleResponse(EnvelopeCodec.ParseResponse(id, frame.Data));
                    break;
                case CanFunc.Stream:
                    HandleStream(EnvelopeCodec.ParseStream(id, frame.Data));
                    break;
                case CanFunc.Event:
                    EventReceived?.Invoke(this, EnvelopeCodec.ParseEvent(id, frame.Data));
                    break;
            }
        }
        catch (Exception ex)
        {
            Error?.Invoke(this, ex.Message);
        }
    }

    private void HandleTelemetry(TelemetryRaw t)
    {
        if (Enum.IsDefined(typeof(MpccChannel), t.Channel) && MpccTables.Channels.TryGetValue((MpccChannel)t.Channel, out var info))
        {
            if (t.Valid)
                Values.Set($"AVG_{MpccNames.Of((MpccChannel)t.Channel)}", t.Value * info.Scale);
        }
        TelemetryReceived?.Invoke(this, t);
    }

    private void HandleResponse(ResponseMessage r)
    {
        if (r.Opcode == (byte)MpccReqOp.GetStatus && r.Status == StatusCode.Ok)
        {
            var st = DeviceStatusReport.Parse(r.Data);
            Values.Set("STATUS_SYS", st.Sys);
            Values.Set("STATUS_ERR", st.Err);
            Values.Set("STATUS_NHEALTHY", st.NHealthy);
            Values.Set("STATUS_CRITICAL", st.Critical ? 1 : 0);
            DeviceStatusReceived?.Invoke(this, st);
        }
        else if (r.Opcode == (byte)MpccReqOp.GetTime && r.Status == StatusCode.Ok && r.Data.Length >= 7)
        {
            TimeReceived?.Invoke(this, ParseTime(r.Data));
        }
        ResponseReceived?.Invoke(this, r);
    }

    private void HandleStream(StreamSegment seg)
    {
        var data = _asm.Push(seg);
        if (data is null) return;

        if (seg.Obj == (byte)MpccReqOp.ListSensors)
        {
            var map = new Dictionary<byte, byte>();
            for (var i = 0; i + 1 < data.Length; i += 2)
                map[data[i]] = data[i + 1];
            SensorsListReceived?.Invoke(this, map);
        }
        else if (seg.Obj == (byte)MpccReqOp.ReadSensor)
        {
            var readings = new List<MpccSensorReading>();
            for (var i = 0; i + 5 <= data.Length; i += 5)
            {
                var qtyByte = data[i];
                var raw = BitConverter.ToInt32(data, i + 1);
                if (Enum.IsDefined(typeof(MpccQuantity), qtyByte) && MpccTables.Quantities.TryGetValue((MpccQuantity)qtyByte, out var qi))
                {
                    var value = raw * qi.Scale;
                    readings.Add(new MpccSensorReading((MpccQuantity)qtyByte, value, qi.Unit));
                    if (Enum.IsDefined(typeof(MpccSensor), seg.Arg))
                        Values.Set($"{MpccNames.Of((MpccSensor)seg.Arg)}_{MpccNames.Of((MpccQuantity)qtyByte)}", value);
                }
            }
            if (Enum.IsDefined(typeof(MpccSensor), seg.Arg))
                SensorReadingsReceived?.Invoke(this, ((MpccSensor)seg.Arg, readings));
        }
        else if (seg.Obj == (byte)MpccReqOp.GetInfo && data.Length >= 4)
        {
            InfoReceived?.Invoke(this, new MpccDeviceInfo(data[0], data[1], data[2], data[3]));
        }
    }

    private static MpccRtcTime ParseTime(byte[] d) => new(d[0], d[1], d[2], d[3], d[4], 2000 + d[5], d[6]);

    // --- requests (PC -> node) ---------------------------------------------------------------------

    private Task Send(MpccReqOp op, byte arg = 0, ReadOnlyMemory<byte> data = default, CancellationToken ct = default)
        => _bus.SendAsync(EnvelopeCodec.BuildRequest(NodeId, (byte)op, arg, data.Span), ct);

    public Task PingAsync(CancellationToken ct = default) => Send(MpccReqOp.Ping, ct: ct);
    public Task GetInfoAsync(CancellationToken ct = default) => Send(MpccReqOp.GetInfo, ct: ct);
    public Task ReadAvgAsync(MpccChannel channel, CancellationToken ct = default) => Send(MpccReqOp.ReadAvg, (byte)channel, ct: ct);
    public Task ReadSensorAsync(MpccSensor sensor, CancellationToken ct = default) => Send(MpccReqOp.ReadSensor, (byte)sensor, ct: ct);
    public Task ListSensorsAsync(CancellationToken ct = default) => Send(MpccReqOp.ListSensors, ct: ct);
    public Task SaveConfigAsync(CancellationToken ct = default) => Send(MpccReqOp.SaveConfig, ct: ct);
    public Task LoadDefaultsAsync(CancellationToken ct = default) => Send(MpccReqOp.LoadDefaults, ct: ct);
    public Task GetTimeAsync(CancellationToken ct = default) => Send(MpccReqOp.GetTime, ct: ct);
    public Task GetStatusAsync(CancellationToken ct = default) => Send(MpccReqOp.GetStatus, ct: ct);
    public Task ResetAsync(CancellationToken ct = default) => Send(MpccReqOp.Reset, data: new byte[] { 0xA5 }, ct: ct);
    public Task EnterBootloaderAsync(CancellationToken ct = default) => Send(MpccReqOp.EnterBootloader, data: new byte[] { 0xB0 }, ct: ct);

    public Task SetTimeAsync(int sec, int min, int hour, int day, int month, int year, int weekDay = 1, CancellationToken ct = default) =>
        Send(MpccReqOp.SetTime, data: new byte[] { (byte)sec, (byte)min, (byte)hour, (byte)day, (byte)month, (byte)(year % 100), (byte)weekDay }, ct: ct);

    public Task ReadParamAsync(byte paramId, CancellationToken ct = default) => Send(MpccReqOp.ReadParam, paramId, ct: ct);

    public Task WriteParamAsync(ParamDescriptor param, double value, CancellationToken ct = default) =>
        Send(MpccReqOp.WriteParam, param.Id, ParamCodec.Encode(param.Type, value), ct);

    // --- MPCC-only extensions -----------------------------------------------------------------------

    public Task GetOutputAsync(byte outputId = 0, CancellationToken ct = default) => Send(MpccReqOp.GetOutput, outputId, ct: ct);
    public Task SetOutputAsync(bool on, byte outputId = 0, CancellationToken ct = default) =>
        Send(MpccReqOp.SetOutput, outputId, new byte[] { (byte)(on ? 1 : 0) }, ct);
    public Task SetLedAsync(bool r, bool g, bool b, CancellationToken ct = default) =>
        Send(MpccReqOp.SetLed, data: new byte[] { (byte)(r ? 1 : 0), (byte)(g ? 1 : 0), (byte)(b ? 1 : 0) }, ct: ct);
    public Task ReadAdcAsync(byte channel, CancellationToken ct = default) => Send(MpccReqOp.ReadAdc, channel, ct: ct);

    // --- CSV logging (mirrors the reference apps' wide logger, driven from the live dashboard) -------

    public bool IsLogging => _csvLogger is not null;

    public void StartCsvLogging(string path, TimeSpan interval)
    {
        StopCsvLogging();
        var columns = new List<string> { "STATUS_SYS", "STATUS_ERR", "STATUS_NHEALTHY", "STATUS_CRITICAL" };
        columns.InsertRange(0, Enum.GetValues<MpccChannel>().Select(c => $"AVG_{MpccNames.Of(c)}"));
        foreach (var sensor in Enum.GetValues<MpccSensor>())
            foreach (var qty in MpccTables.SensorProvides[sensor])
                columns.Add($"{MpccNames.Of(sensor)}_{MpccNames.Of(qty)}");

        _csvLogger = new WideCsvLogger(path, columns.Distinct());
        var staleness = TimeSpan.FromSeconds(Math.Max(interval.TotalSeconds * 2.5, 5));
        _pollTimer = new System.Threading.Timer(_ =>
        {
            foreach (var s in Enum.GetValues<MpccSensor>()) _ = ReadSensorAsync(s);
            _ = GetStatusAsync();
            _csvLogger?.WriteRow(Values, staleness);
        }, null, TimeSpan.Zero, interval);
    }

    public void StopCsvLogging()
    {
        _pollTimer?.Dispose();
        _pollTimer = null;
        _csvLogger?.Dispose();
        _csvLogger = null;
    }

    public void Dispose()
    {
        StopCsvLogging();
        _bus.FrameReceived -= OnFrameReceived;
    }
}
