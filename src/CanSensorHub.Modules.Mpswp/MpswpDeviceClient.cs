using CanSensorHub.Core.Can;
using CanSensorHub.Core.Logging;
using CanSensorHub.Core.Protocol;
using CanSensorHub.Modules.Mpswp.Protocol;

namespace CanSensorHub.Modules.Mpswp;

/// <summary>One quantity reading from READ_SENSOR, including its status against LPL/UPL/LTL/UTL (see <see cref="MpswpLimits"/>). ERR_* readings are still delivered here (diagnostic) even though the device excluded them from fusion.</summary>
public sealed record MpswpSensorReading(MpswpQuantity Quantity, double Value, string Unit, MpswpReadingStatus Status)
{
    public bool IsRejected => Status is MpswpReadingStatus.ErrLow or MpswpReadingStatus.ErrHigh;
    public bool IsWarning => Status is MpswpReadingStatus.WarnLow or MpswpReadingStatus.WarnHigh;
}
/// <summary>
/// GET_INFO reply. The four legacy fields (ProtocolVersion..Node) are always present — every firmware
/// generation this app has ever spoken to sends at least those, at the same offsets (protocol.yaml's
/// info_layout keeps [0:4] stable on purpose). HwMajor.. are only populated when the device's GET_INFO
/// frame is long enough to carry the newer, extended layout (21 B) — older firmware simply leaves them
/// null, and the Pulpit tab shows a note instead of guessing.
/// </summary>
public sealed record MpswpDeviceInfo(
    byte ProtocolVersion, byte FwMajor, byte FwMinor, byte Node,
    byte? HwMajor = null, byte? HwMinor = null, ushort? BuildRevision = null,
    MpswpInfoBuildFlags? BuildFlags = null, byte[]? Uid = null,
    ushort? DeviceType = null, byte? ProfileVersion = null, uint? Capabilities = null)
{
    public bool HasExtendedInfo => HwMajor.HasValue;
    public string? UidHex => Uid is null ? null : Convert.ToHexString(Uid);

    /// <summary>Generacja protokołu węzła, rozpoznana z długości odpowiedzi GET_INFO.</summary>
    public DeviceProfile Profile => ProfileVersion switch
    {
        >= (byte)DeviceProfile.Common => DeviceProfile.Common,
        _ when HwMajor.HasValue => DeviceProfile.Legacy21B,
        _ => DeviceProfile.Legacy4B,
    };

    public bool Supports(DeviceCapability capability) =>
        Capabilities is { } caps && (caps & (uint)capability) != 0;

    /// <summary>
    /// Węzeł zgłasza typ urządzenia inny niż obsługiwany przez ten moduł. Sytuacja oznacza, że pod
    /// danym adresem pracuje urządzenie innego rodzaju — interpretacja jego telemetrii i parametrów
    /// według tablic MPSWP dałaby wartości pozornie poprawne.
    /// </summary>
    public bool DeviceTypeMismatch => DeviceType is { } dt && dt != MpswpInfo.DeviceType;

    public static MpswpDeviceInfo From(DeviceIdentity id) => new(
        id.ProtocolVersion, id.FwMajor, id.FwMinor, id.Node,
        id.HwMajor, id.HwMinor, id.BuildRevision,
        id.BuildFlags is { } bf ? (MpswpInfoBuildFlags)bf : null,
        id.Uid, id.DeviceType, id.ProfileVersion, id.Capabilities);
}
public sealed record MpswpRtcTime(int Second, int Minute, int Hour, int Day, int Month, int Year, int WeekDay);
public sealed record MpswpCalibResult(StatusCode Status, byte TunCap, uint LcoHz);
/// <summary>Decoded EVENT SENSOR_RANGE (0x0B) — an edge in a (sensor, quantity) reading's status against its limits.</summary>
public sealed record MpswpRangeEvent(MpswpSensor Sensor, MpswpQuantity Quantity, MpswpReadingStatus Status, double Value, string Unit);

/// <summary>
/// Typed request/response layer for one MPSWP node, built on the shared envelope codec. Subscribes to
/// the shell's <see cref="CanBusService"/> and filters by <see cref="NodeId"/>, so multiple devices (MPCC
/// and/or MPSWP instances) can share one physical connection without interfering.
/// </summary>
public sealed class MpswpDeviceClient : IDisposable
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
    public event EventHandler<(MpswpSensor Sensor, IReadOnlyList<MpswpSensorReading> Readings)>? SensorReadingsReceived;
    public event EventHandler<MpswpDeviceInfo>? InfoReceived;
    public event EventHandler<DeviceStatusReport>? DeviceStatusReceived;
    public event EventHandler<MpswpRtcTime>? TimeReceived;
    public event EventHandler<MpswpCalibResult>? AntennaCalibrated;
    public event EventHandler<MpswpRangeEvent>? SensorRangeChanged;
    public event EventHandler<string>? Error;

    public MpswpDeviceClient(CanBusService bus, byte nodeId)
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
                    HandleEvent(EnvelopeCodec.ParseEvent(id, frame.Data));
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
        if (Enum.IsDefined(typeof(MpswpChannel), t.Channel) && MpswpTables.Channels.TryGetValue((MpswpChannel)t.Channel, out var info) && t.Valid)
            Values.Set($"AVG_{MpswpNames.Of((MpswpChannel)t.Channel)}", t.Value * info.Scale);
        TelemetryReceived?.Invoke(this, t);
    }

    private void HandleResponse(ResponseMessage r)
    {
        if (r.Opcode == (byte)MpswpReqOp.GetStatus && r.Status == StatusCode.Ok)
        {
            var st = DeviceStatusReport.Parse(r.Data);
            Values.Set("STATUS_SYS", st.Sys);
            Values.Set("STATUS_ERR", st.Err);
            Values.Set("STATUS_NHEALTHY", st.NHealthy);
            Values.Set("STATUS_CRITICAL", st.Critical ? 1 : 0);
            DeviceStatusReceived?.Invoke(this, st);
        }
        else if (r.Opcode == (byte)MpswpReqOp.GetTime && r.Status == StatusCode.Ok && r.Data.Length >= 7)
        {
            TimeReceived?.Invoke(this, ParseTime(r.Data));
        }
        ResponseReceived?.Invoke(this, r);
    }

    private void HandleEvent(EventMessage e)
    {
        if (e.Code == (byte)MpswpEventCode.CalibDone && e.Data.Length >= 6)
        {
            var status = (StatusCode)e.Data[0];
            var tunCap = e.Data[1];
            var lco = BitConverter.ToUInt32(e.Data, 2);
            AntennaCalibrated?.Invoke(this, new MpswpCalibResult(status, tunCap, lco));
        }
        else if (e.Code == (byte)MpswpEventCode.SensorRange && e.Data.Length >= 6 && Enum.IsDefined(typeof(MpswpSensor), e.Source))
        {
            var qtyByte = e.Data[0];
            if (Enum.IsDefined(typeof(MpswpQuantity), qtyByte) && MpswpTables.Quantities.TryGetValue((MpswpQuantity)qtyByte, out var qi))
            {
                var status = (MpswpReadingStatus)e.Data[1];
                var raw = BitConverter.ToInt32(e.Data, 2);
                SensorRangeChanged?.Invoke(this, new MpswpRangeEvent((MpswpSensor)e.Source, (MpswpQuantity)qtyByte, status, raw * qi.Scale, qi.Unit));
            }
        }
        EventReceived?.Invoke(this, e);
    }

    private void HandleStream(StreamSegment seg)
    {
        var data = _asm.Push(seg);
        if (data is null) return;

        if (seg.Obj == (byte)MpswpReqOp.ListSensors)
        {
            var map = new Dictionary<byte, byte>();
            for (var i = 0; i + 1 < data.Length; i += 2)
                map[data[i]] = data[i + 1];
            SensorsListReceived?.Invoke(this, map);
        }
        else if (seg.Obj == (byte)MpswpReqOp.ReadSensor)
        {
            // 6 B/wielkość: [qty u8, value i32 LE, status u8]. Odrzucone odczyty (poza LTL/UTL) SA
            // wysyłane — diagnostyka: widać którą (odrzuconą) wartość czujnik faktycznie zwrócił.
            var readings = new List<MpswpSensorReading>();
            for (var i = 0; i + 6 <= data.Length; i += 6)
            {
                var qtyByte = data[i];
                var raw = BitConverter.ToInt32(data, i + 1);
                var status = (MpswpReadingStatus)data[i + 5];
                if (Enum.IsDefined(typeof(MpswpQuantity), qtyByte) && MpswpTables.Quantities.TryGetValue((MpswpQuantity)qtyByte, out var qi))
                {
                    var value = raw * qi.Scale;
                    var reading = new MpswpSensorReading((MpswpQuantity)qtyByte, value, qi.Unit, status);
                    readings.Add(reading);
                    // Odrzucone wartości nie trafiają do magazynu (CSV/wykresy) — pozostają widoczne
                    // wyłącznie diagnostycznie przez SensorReadingsReceived, nie zniekształcają logów.
                    if (!reading.IsRejected && Enum.IsDefined(typeof(MpswpSensor), seg.Arg))
                        Values.Set($"{MpswpNames.Of((MpswpSensor)seg.Arg)}_{MpswpNames.Of((MpswpQuantity)qtyByte)}", value);
                }
            }
            if (Enum.IsDefined(typeof(MpswpSensor), seg.Arg))
                SensorReadingsReceived?.Invoke(this, ((MpswpSensor)seg.Arg, readings));
        }
        else if (seg.Obj == (byte)MpswpReqOp.GetInfo && data.Length >= DeviceIdentity.Length4B)
        {
            // Dekodowanie ramki identyfikacyjnej należy do warstwy wspólnej: układ pól [0:20] jest
            // zamrożony i identyczny dla każdego węzła, a profil rozpoznaje się po długości
            // odpowiedzi (4, 21 albo 28 bajtów).
            InfoReceived?.Invoke(this, MpswpDeviceInfo.From(DeviceIdentity.Parse(data)));
        }
    }

    private static MpswpRtcTime ParseTime(byte[] d) => new(d[0], d[1], d[2], d[3], d[4], 2000 + d[5], d[6]);

    // --- requests (PC -> node) ---------------------------------------------------------------------

    private Task Send(MpswpReqOp op, byte arg = 0, ReadOnlyMemory<byte> data = default, CancellationToken ct = default)
        => _bus.SendAsync(EnvelopeCodec.BuildRequest(NodeId, (byte)op, arg, data.Span), ct);

    public Task PingAsync(CancellationToken ct = default) => Send(MpswpReqOp.Ping, ct: ct);
    public Task GetInfoAsync(CancellationToken ct = default) => Send(MpswpReqOp.GetInfo, ct: ct);
    public Task ReadAvgAsync(MpswpChannel channel, CancellationToken ct = default) => Send(MpswpReqOp.ReadAvg, (byte)channel, ct: ct);
    public Task ReadSensorAsync(MpswpSensor sensor, CancellationToken ct = default) => Send(MpswpReqOp.ReadSensor, (byte)sensor, ct: ct);
    public Task ListSensorsAsync(CancellationToken ct = default) => Send(MpswpReqOp.ListSensors, ct: ct);
    public Task SaveConfigAsync(CancellationToken ct = default) => Send(MpswpReqOp.SaveConfig, ct: ct);
    /// <summary>
    /// Przywraca wartości domyślne wszystkich parametrów. Komenda kasuje także kalibrację, dlatego
    /// firmware wymaga bajtu zabezpieczającego — ramka bez niego jest odrzucana kodem ERR_BAD_PARAM.
    /// </summary>
    public Task LoadDefaultsAsync(CancellationToken ct = default) =>
        Send(MpswpReqOp.LoadDefaults, data: new byte[] { CommandGuard.LoadDefaults }, ct: ct);
    public Task GetTimeAsync(CancellationToken ct = default) => Send(MpswpReqOp.GetTime, ct: ct);
    public Task GetStatusAsync(CancellationToken ct = default) => Send(MpswpReqOp.GetStatus, ct: ct);
    public Task ResetAsync(CancellationToken ct = default) =>
        Send(MpswpReqOp.Reset, data: new byte[] { CommandGuard.Reset }, ct: ct);
    public Task EnterBootloaderAsync(CancellationToken ct = default) =>
        Send(MpswpReqOp.EnterBootloader, data: new byte[] { CommandGuard.EnterBootloader }, ct: ct);
    public Task CalibrateAntennaAsync(CancellationToken ct = default) => Send(MpswpReqOp.CalibAntenna, ct: ct);

    public Task SetTimeAsync(int sec, int min, int hour, int day, int month, int year, int weekDay = 1, CancellationToken ct = default) =>
        Send(MpswpReqOp.SetTime, data: new byte[] { (byte)sec, (byte)min, (byte)hour, (byte)day, (byte)month, (byte)(year % 100), (byte)weekDay }, ct: ct);

    public Task ReadParamAsync(byte paramId, CancellationToken ct = default) => Send(MpswpReqOp.ReadParam, paramId, ct: ct);

    public Task WriteParamAsync(ParamDescriptor param, double value, CancellationToken ct = default) =>
        Send(MpswpReqOp.WriteParam, param.Id, ParamCodec.Encode(param.Type, value), ct);

    // --- CSV logging (mirrors the reference apps' wide logger, driven from the live dashboard) -------

    public bool IsLogging => _csvLogger is not null;

    public void StartCsvLogging(string path, TimeSpan interval)
    {
        StopCsvLogging();
        var columns = new List<string> { "STATUS_SYS", "STATUS_ERR", "STATUS_NHEALTHY", "STATUS_CRITICAL" };
        columns.InsertRange(0, Enum.GetValues<MpswpChannel>().Select(c => $"AVG_{MpswpNames.Of(c)}"));
        foreach (var sensor in Enum.GetValues<MpswpSensor>())
            foreach (var qty in MpswpTables.SensorProvides[sensor])
                columns.Add($"{MpswpNames.Of(sensor)}_{MpswpNames.Of(qty)}");

        _csvLogger = new WideCsvLogger(path, columns.Distinct());
        var staleness = TimeSpan.FromSeconds(Math.Max(interval.TotalSeconds * 2.5, 5));
        _pollTimer = new System.Threading.Timer(_ =>
        {
            foreach (var s in Enum.GetValues<MpswpSensor>()) _ = ReadSensorAsync(s);
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
