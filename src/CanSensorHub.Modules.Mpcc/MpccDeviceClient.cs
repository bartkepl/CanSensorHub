using CanSensorHub.Core.Can;
using CanSensorHub.Core.Logging;
using CanSensorHub.Core.Protocol;
using CanSensorHub.Modules.Mpcc.Protocol;

namespace CanSensorHub.Modules.Mpcc;

/// <summary>Jeden odczyt z READ_SENSOR wraz z bajtem statusu rekordu <c>sensor_reading</c>. MPCC nie prowadzi limitów, więc status jest w praktyce zawsze <see cref="MpccReadingStatus.Ok"/> — pole niesie go mimo to, bo należy do układu rekordu, a nie do zdolności węzła.</summary>
public sealed record MpccSensorReading(MpccQuantity Quantity, double Value, string Unit, MpccReadingStatus Status);
/// <summary>
/// Odpowiedź GET_INFO. Cztery pola zgodne wstecz (ProtocolVersion..Node) są obecne zawsze — każda
/// generacja firmware wysyła co najmniej je, pod tymi samymi przesunięciami (układ info_layout
/// warstwy wspólnej trzyma [0:4] nieruchomo). Pola od HwMajor wzwyż wypełnia dopiero ramka
/// rozszerzona (21 B), a DeviceType/ProfileVersion/Capabilities — ramka profilu 2 (28 B). Starsze
/// firmware pozostawia je puste, a widok pokazuje wtedy notę zamiast zgadywać.
/// </summary>
public sealed record MpccDeviceInfo(
    byte ProtocolVersion, byte FwMajor, byte FwMinor, byte Node,
    byte? HwMajor = null, byte? HwMinor = null, ushort? BuildRevision = null,
    MpccInfoBuildFlags? BuildFlags = null, byte[]? Uid = null,
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
    /// według tablic MPCC dałaby wartości pozornie poprawne.
    /// </summary>
    public bool DeviceTypeMismatch => DeviceType is { } dt && dt != MpccInfo.DeviceType;

    public static MpccDeviceInfo From(DeviceIdentity id) => new(
        id.ProtocolVersion, id.FwMajor, id.FwMinor, id.Node,
        id.HwMajor, id.HwMinor, id.BuildRevision,
        id.BuildFlags is { } bf ? (MpccInfoBuildFlags)bf : null,
        id.Uid, id.DeviceType, id.ProfileVersion, id.Capabilities);
}
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
        _asm.TransferAbandoned += OnTransferAbandoned;
        _bus.FrameReceived += OnFrameReceived;
    }

    // Porzucony transfer oznacza zgubioną ramkę STREAM: odpowiedź przepada zamiast dotrzeć
    // sklejona z danymi innego odpytania. Zgłoszenie trafia do dziennika zdarzeń, żeby
    // utrata ramek na magistrali była widoczna, a nie objawiała się tylko lukami na wykresie.
    private void OnTransferAbandoned(object? sender, StreamTransferAbandoned a)
    {
        var op = Enum.IsDefined(typeof(MpccReqOp), a.Obj) ? ((MpccReqOp)a.Obj).ToString() : $"0x{a.Obj:X2}";
        var why = a.Reason == StreamAbandonReason.Timeout ? "przekroczony czas oczekiwania" : "nadszedł kolejny transfer";
        var missing = a.MissingIndices.Length > 0 ? string.Join(", ", a.MissingIndices) : "—";
        Error?.Invoke(this, $"Porzucono niekompletną odpowiedź {op} (ARG=0x{a.Arg:X2}): brak segmentów {missing}; {why}.");
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
            // Rekord 'sensor_reading' warstwy wspólnej ma 6 B: [QTY u8, VALUE i32 LE,
            // READING_STATUS u8]. Krok pętli musi odpowiadać długości rekordu co do bajtu —
            // rozjazd nie kończy się błędem, tylko cichym przesunięciem kolejnych wartości.
            var readings = new List<MpccSensorReading>();
            for (var i = 0; i + 6 <= data.Length; i += 6)
            {
                var qtyByte = data[i];
                var raw = BitConverter.ToInt32(data, i + 1);
                var status = (MpccReadingStatus)data[i + 5];
                if (Enum.IsDefined(typeof(MpccQuantity), qtyByte) && MpccTables.Quantities.TryGetValue((MpccQuantity)qtyByte, out var qi))
                {
                    var value = raw * qi.Scale;
                    readings.Add(new MpccSensorReading((MpccQuantity)qtyByte, value, qi.Unit, status));
                    if (Enum.IsDefined(typeof(MpccSensor), seg.Arg))
                        Values.Set($"{MpccNames.Of((MpccSensor)seg.Arg)}_{MpccNames.Of((MpccQuantity)qtyByte)}", value);
                }
            }
            if (Enum.IsDefined(typeof(MpccSensor), seg.Arg))
                SensorReadingsReceived?.Invoke(this, ((MpccSensor)seg.Arg, readings));
        }
        else if (seg.Obj == (byte)MpccReqOp.GetInfo && data.Length >= DeviceIdentity.Length4B)
        {
            // Dekodowanie ramki identyfikacyjnej należy do warstwy wspólnej: układ pól [0:20] jest
            // zamrożony i identyczny dla każdego węzła, a profil rozpoznaje się po długości
            // odpowiedzi (4, 21 albo 28 bajtów).
            InfoReceived?.Invoke(this, MpccDeviceInfo.From(DeviceIdentity.Parse(data)));
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
    // Bez bajtu zabezpieczającego firmware odrzuca LOAD_DEFAULTS kodem ERR_BAD_PARAM (wsc_app.c).
    public Task LoadDefaultsAsync(CancellationToken ct = default) =>
        Send(MpccReqOp.LoadDefaults, data: new byte[] { CommandGuard.LoadDefaults }, ct: ct);
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
