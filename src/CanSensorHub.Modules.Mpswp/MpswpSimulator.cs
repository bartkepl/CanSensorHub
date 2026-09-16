using CanSensorHub.Core.Can;
using CanSensorHub.Core.Protocol;
using CanSensorHub.Modules.Mpswp.Protocol;

namespace CanSensorHub.Modules.Mpswp;

/// <summary>
/// Fake MPSWP node attached to a <see cref="SimulatedCanBus"/> so the module's UI can be exercised
/// without a WeAct adapter or real hardware: answers every REQUEST opcode the reference firmware
/// supports (including antenna auto-calibration) and broadcasts synthetic weather telemetry.
/// </summary>
public sealed class MpswpSimulator : IDisposable
{
    private readonly SimulatedTransport _ep;
    private readonly byte _node;
    private readonly Timer _timer;
    private readonly Dictionary<byte, double> _params;
    private readonly DateTime _start = DateTime.UtcNow;
    private byte _seq;

    public MpswpSimulator(SimulatedCanBus bus, byte node)
    {
        _node = node;
        _params = MpswpParams.All.ToDictionary(p => p.Id, p => p.Default);
        _ep = bus.CreateEndpoint($"MPSWP (symulator) NODE=0x{node:X2}");
        _ep.FrameReceived += OnFrame;
        _timer = new Timer(OnTick, null, TimeSpan.FromMilliseconds(300), TimeSpan.FromMilliseconds(1000));
        SendEvent(MpswpEventSource.System, MpswpEventCode.Boot, [0x00, MpswpInfo.ProtocolVersion, MpswpInfo.FwVersionMajor, MpswpInfo.FwVersionMinor, 0x01, 0x00]);
    }

    private double T => (DateTime.UtcNow - _start).TotalSeconds;

    private void OnTick(object? state)
    {
        void Emit(MpswpChannel ch, double value)
        {
            var scale = MpswpTables.Channels[ch].Scale;
            var raw = (int)Math.Round(value / scale);
            var payload = TelemetryRaw.Build(_seq, raw, nSensors: 3, valid: true, clamped: false, qual: 0);
            Send(EnvelopeCodec.BuildTelemetry(_node, (byte)ch, payload));
        }

        Emit(MpswpChannel.Temp, 22.0 + 3.0 * Math.Sin(T / 30.0));
        Emit(MpswpChannel.Hum, 50.0 + 15.0 * Math.Sin(T / 45.0 + 1.0));
        Emit(MpswpChannel.Press, 101325 + 200 * Math.Sin(T / 90.0));
        Emit(MpswpChannel.VocIndex, 100 + 30 * Math.Sin(T / 20.0));
        Emit(MpswpChannel.NoxIndex, 3 + 2 * Math.Sin(T / 25.0));
        Emit(MpswpChannel.Iaq, 50 + 20 * Math.Sin(T / 33.0));
        unchecked { _seq++; }

        if (((int)T) % 10 == 0)
            SendEvent(MpswpEventSource.System, MpswpEventCode.Heartbeat,
                [.. BitConverter.GetBytes((ushort)T), 0x00, 0x00, 0x00, 0x0B, 0x01]);
    }

    private void OnFrame(object? sender, CanFrameReceivedEventArgs e)
    {
        var id = CanId.Unpack(e.Frame.Id);
        if (id.Func != (byte)CanFunc.Request || (id.Node != _node && id.Node != MpswpNodes.Broadcast)) return;
        var arg = id.Arg;
        var data = e.Frame.Data;
        var op = (MpswpReqOp)id.Obj;

        switch (op)
        {
            case MpswpReqOp.Ping:
                RespondOk(op, arg);
                break;
            case MpswpReqOp.GetInfo:
                SendStream(op, arg, BuildInfoPayload());
                break;
            case MpswpReqOp.ReadParam:
                if (_params.TryGetValue(arg, out var pv) && FindParam(arg) is { } pd)
                    RespondOk(op, arg, ParamCodec.Encode(pd.Type, pv));
                else
                    Respond(op, arg, StatusCode.ErrBadParam);
                break;
            case MpswpReqOp.WriteParam:
                var wpd = FindParam(arg);
                if (wpd is null) { Respond(op, arg, StatusCode.ErrBadParam); break; }
                var value = ParamCodec.Decode(wpd.Type, data);
                if (value < wpd.Min || value > wpd.Max) { Respond(op, arg, StatusCode.ErrOutOfRange); break; }
                _params[arg] = value;
                RespondOk(op, arg);
                break;
            case MpswpReqOp.ListSensors:
                SendStream(op, arg, Enum.GetValues<MpswpSensor>().SelectMany(s => new byte[] { (byte)s, 0 }).ToArray());
                break;
            case MpswpReqOp.ReadSensor:
                HandleReadSensor(arg);
                break;
            case MpswpReqOp.SaveConfig:
                RespondOk(op, arg);
                break;
            case MpswpReqOp.LoadDefaults:
                foreach (var p in MpswpParams.All) _params[p.Id] = p.Default;
                RespondOk(op, arg);
                break;
            case MpswpReqOp.GetTime:
                var now = DateTime.Now;
                RespondOk(op, arg, [(byte)now.Second, (byte)now.Minute, (byte)now.Hour, (byte)now.Day, (byte)now.Month, (byte)(now.Year % 100), (byte)((int)now.DayOfWeek + 1)]);
                break;
            case MpswpReqOp.SetTime:
                RespondOk(op, arg);
                break;
            case MpswpReqOp.GetStatus:
                RespondOk(op, arg, [0xFF, 0x00, 0x0F, 0xFF, 0x00, 0x00, 0x0B]);
                break;
            case MpswpReqOp.Reset:
                if (data.Length > 0 && data[0] == 0xA5)
                {
                    RespondOk(op, arg);
                    SendEvent(MpswpEventSource.System, MpswpEventCode.Boot, [0x08, MpswpInfo.ProtocolVersion, MpswpInfo.FwVersionMajor, MpswpInfo.FwVersionMinor, 0x01, 0x00]);
                }
                else Respond(op, arg, StatusCode.ErrBadParam);
                break;
            case MpswpReqOp.EnterBootloader:
                RespondOk(op, arg); // symulator nie ma bootloadera — akceptuje żądanie, nie resetuje się
                break;
            case MpswpReqOp.CalibAntenna:
                RespondOk(op, arg); // ACK: kalibracja "ruszyła"
                _ = Task.Delay(1200).ContinueWith(_ =>
                {
                    var tunCap = (byte)7;
                    var payload = new byte[6];
                    payload[0] = (byte)StatusCode.Ok;
                    payload[1] = tunCap;
                    BitConverter.GetBytes(500_000u).CopyTo(payload, 2);
                    SendEvent(MpswpEventSource.System, MpswpEventCode.CalibDone, payload);
                });
                break;
        }
    }

    private void HandleReadSensor(byte sensorArg)
    {
        if (!Enum.IsDefined(typeof(MpswpSensor), sensorArg)) { Respond(MpswpReqOp.ReadSensor, sensorArg, StatusCode.ErrSensorAbsent); return; }
        var sensor = (MpswpSensor)sensorArg;
        var payload = new List<byte>();
        void Add(MpswpQuantity q, double value)
        {
            payload.Add((byte)q);
            payload.AddRange(BitConverter.GetBytes((int)Math.Round(value / MpswpTables.Quantities[q].Scale)));
            payload.Add((byte)MpswpLimits.Check(sensor, q, value));
        }
        switch (sensor)
        {
            case MpswpSensor.Htu21D:
                Add(MpswpQuantity.Temp, 22.0 + 3.0 * Math.Sin(T / 30.0));
                Add(MpswpQuantity.Hum, 50.0 + 15.0 * Math.Sin(T / 45.0 + 1.0));
                break;
            case MpswpSensor.Sht45:
                Add(MpswpQuantity.Temp, 22.2 + 3.0 * Math.Sin(T / 30.0 + 0.2));
                Add(MpswpQuantity.Hum, 49.5 + 15.0 * Math.Sin(T / 45.0 + 1.2));
                break;
            case MpswpSensor.Mpl3115A2:
                Add(MpswpQuantity.Temp, 21.8 + 3.0 * Math.Sin(T / 30.0 - 0.3));
                Add(MpswpQuantity.Press, 101325 + 200 * Math.Sin(T / 90.0));
                break;
            case MpswpSensor.Lps25Hb:
                Add(MpswpQuantity.Temp, 22.1 + 3.0 * Math.Sin(T / 30.0 + 0.1));
                Add(MpswpQuantity.Press, 101300 + 200 * Math.Sin(T / 90.0 + 0.2));
                break;
            case MpswpSensor.Bme680:
                Add(MpswpQuantity.Temp, 22.3 + 3.0 * Math.Sin(T / 30.0 + 0.4));
                Add(MpswpQuantity.Hum, 50.5 + 15.0 * Math.Sin(T / 45.0 + 0.8));
                Add(MpswpQuantity.Press, 101310 + 200 * Math.Sin(T / 90.0 - 0.1));
                Add(MpswpQuantity.GasRes, 50000 + 5000 * Math.Sin(T / 15.0));
                break;
            case MpswpSensor.Sgp41:
                Add(MpswpQuantity.VocRaw, 25000 + 500 * Math.Sin(T / 20.0));
                Add(MpswpQuantity.NoxRaw, 15000 + 200 * Math.Sin(T / 25.0));
                break;
            case MpswpSensor.Sts31Cpu:
                Add(MpswpQuantity.Temp, 24.0 + 2.0 * Math.Sin(T / 28.0));
                break;
            case MpswpSensor.Sts31Sens:
                Add(MpswpQuantity.Temp, 21.9 + 3.0 * Math.Sin(T / 30.0 - 0.15));
                break;
            case MpswpSensor.Tmp117:
                Add(MpswpQuantity.Temp, 22.0 + 3.0 * Math.Sin(T / 30.0));
                break;
            case MpswpSensor.Mcu:
                Add(MpswpQuantity.McuTemp, 30.0 + 2.0 * Math.Sin(T / 35.0));
                Add(MpswpQuantity.Vdda, 3.30);
                Add(MpswpQuantity.Vbat, 3.05);
                break;
            case MpswpSensor.As3935:
            case MpswpSensor.Pcf8574:
                break; // zdarzeniowe / brak wielkości ciągłych
        }
        SendStream(MpswpReqOp.ReadSensor, sensorArg, payload.ToArray());
    }

    /// <summary>
    /// Ramka identyfikacyjna profilu 2 (28 B), budowana wg układu info_layout warstwy wspólnej.
    /// Symulator zgłasza tę samą długość co rzeczywiste urządzenie po migracji, dzięki czemu
    /// rozpoznawanie profilu po długości odpowiedzi jest sprawdzane także bez sprzętu.
    /// Przesunięcia [0:20] pozostają zgodne ze starszymi profilami.
    /// </summary>
    private byte[] BuildInfoPayload()
    {
        var d = new byte[DeviceIdentity.Length28B];
        d[DeviceIdentity.OffProtocolVersion] = MpswpInfo.ProtocolVersion;
        d[DeviceIdentity.OffFwMajor] = MpswpInfo.FwVersionMajor;
        d[DeviceIdentity.OffFwMinor] = MpswpInfo.FwVersionMinor;
        d[DeviceIdentity.OffNode] = _node;
        d[DeviceIdentity.OffHwMajor] = MpswpInfo.HwVersionMajor;
        d[DeviceIdentity.OffHwMinor] = MpswpInfo.HwVersionMinor;
        BitConverter.GetBytes(MpswpInfo.BuildRevision).CopyTo(d, DeviceIdentity.OffBuildRevision);
        d[DeviceIdentity.OffBuildFlags] = (byte)MpswpInfoBuildFlags.None;

        // Sygnatura symulatora w miejscu UID. Nie jest to numer rzeczywistego układu — pozwala
        // odróżnić węzeł symulowany od sprzętowego w dzienniku i w panelu informacyjnym.
        BitConverter.GetBytes(0x53494Du).CopyTo(d, DeviceIdentity.OffUid);
        BitConverter.GetBytes((uint)_node).CopyTo(d, DeviceIdentity.OffUid + 4);
        BitConverter.GetBytes(0u).CopyTo(d, DeviceIdentity.OffUid + 8);

        BitConverter.GetBytes(MpswpInfo.DeviceType).CopyTo(d, DeviceIdentity.OffDeviceType);
        d[DeviceIdentity.OffProfileVersion] = MpswpInfo.ProfileVersion;
        BitConverter.GetBytes(MpswpCapabilities.Mask).CopyTo(d, DeviceIdentity.OffCapabilities);
        return d;
    }

    private ParamDescriptor? FindParam(byte id) => MpswpParams.All.FirstOrDefault(p => p.Id == id);

    private void RespondOk(MpswpReqOp op, byte arg, ReadOnlySpan<byte> data = default) => Respond(op, arg, StatusCode.Ok, data);
    private void Respond(MpswpReqOp op, byte arg, StatusCode status, ReadOnlySpan<byte> data = default) =>
        Send(EnvelopeCodec.BuildResponse(_node, (byte)op, arg, status, data));

    private void SendStream(MpswpReqOp op, byte arg, byte[] payload)
    {
        const int chunk = 7;
        if (payload.Length == 0) { Send(EnvelopeCodec.BuildStream(_node, (byte)op, arg, 0, true, [])); return; }
        for (var i = 0; i < payload.Length; i += chunk)
        {
            var n = Math.Min(chunk, payload.Length - i);
            var last = i + n >= payload.Length;
            Send(EnvelopeCodec.BuildStream(_node, (byte)op, arg, i / chunk, last, payload.AsSpan(i, n)));
        }
    }

    private void SendEvent(byte source, MpswpEventCode code, ReadOnlySpan<byte> data) =>
        Send(EnvelopeCodec.BuildEvent(_node, source, (byte)code, data));

    private void Send(CanFrame frame) => _ = _ep.SendAsync(frame);

    public void Dispose()
    {
        _timer.Dispose();
        _ep.Dispose();
    }
}
