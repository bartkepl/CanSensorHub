using CanSensorHub.Core.Can;
using CanSensorHub.Core.Protocol;
using CanSensorHub.Modules.Mpcc.Protocol;

namespace CanSensorHub.Modules.Mpcc;

/// <summary>
/// Fake MPCC node attached to a <see cref="SimulatedCanBus"/> so the module's UI can be exercised (and
/// demoed) without a WeAct adapter or real hardware: answers every REQUEST opcode the reference firmware
/// supports and broadcasts synthetic, slowly-drifting telemetry. Does not implement the bootloader —
/// flashing always needs a real (or eventually simulated-at-that-level) device.
/// </summary>
public sealed class MpccSimulator : IDisposable
{
    private readonly SimulatedTransport _ep;
    private readonly byte _node;
    private readonly Timer _timer;
    private readonly Dictionary<byte, double> _params;
    private readonly DateTime _start = DateTime.UtcNow;
    private bool _outputOn;
    private byte _seq;

    public MpccSimulator(SimulatedCanBus bus, byte node)
    {
        _node = node;
        _params = MpccParams.All.ToDictionary(p => p.Id, p => p.Default);
        _ep = bus.CreateEndpoint($"MPCC (symulator) NODE=0x{node:X2}");
        _ep.FrameReceived += OnFrame;
        _timer = new Timer(OnTick, null, TimeSpan.FromMilliseconds(300), TimeSpan.FromMilliseconds(1000));
        SendEvent(MpccEventSource.System, MpccEventCode.Boot, [0x00, MpccInfo.ProtocolVersion, MpccInfo.FwVersionMajor, MpccInfo.FwVersionMinor, 0x01, 0x00]);
    }

    private double T => (DateTime.UtcNow - _start).TotalSeconds;

    private void OnTick(object? state)
    {
        void Emit(MpccChannel ch, double value)
        {
            var scale = MpccTables.Channels[ch].Scale;
            var raw = (int)Math.Round(value / scale);
            var payload = TelemetryRaw.Build(_seq, raw, nSensors: 1, valid: true, clamped: false, qual: 0);
            Send(EnvelopeCodec.BuildTelemetry(_node, (byte)ch, payload));
        }

        Emit(MpccChannel.Temp, 23.0 + 3.0 * Math.Sin(T / 30.0));
        Emit(MpccChannel.Adc0, 3.30 + 0.20 * Math.Sin(T / 12.0));
        Emit(MpccChannel.Adc1, 2.50 + 0.15 * Math.Sin(T / 14.0 + 1));
        Emit(MpccChannel.Adc2, 4.10 + 0.10 * Math.Sin(T / 16.0 + 2));
        Emit(MpccChannel.Adc3, 1.80 + 0.05 * Math.Sin(T / 18.0 + 3));
        Emit(MpccChannel.Adc4, 12.0 + 1.0 * Math.Sin(T / 20.0));
        Emit(MpccChannel.Adc5, 24.0 + 0.5 * Math.Sin(T / 22.0 + 1));
        Emit(MpccChannel.Adc6, 5.0 + 0.3 * Math.Sin(T / 24.0 + 2));
        Emit(MpccChannel.Vdda, 3.00 + 0.005 * Math.Sin(T / 40.0));
        Emit(MpccChannel.McuTemp, 32.0 + 2.0 * Math.Sin(T / 35.0 + 0.5));
        unchecked { _seq++; }

        if (((int)T) % 10 == 0)
            SendEvent(MpccEventSource.System, MpccEventCode.Heartbeat,
                [.. BitConverter.GetBytes((ushort)T), 0x00, 0x00, 0x00, 0x03, 0x01]);
    }

    private void OnFrame(object? sender, CanFrameReceivedEventArgs e)
    {
        var id = CanId.Unpack(e.Frame.Id);
        if (id.Func != (byte)CanFunc.Request || (id.Node != _node && id.Node != MpccNodes.Broadcast)) return;
        var arg = id.Arg;
        var data = e.Frame.Data;
        var op = (MpccReqOp)id.Obj;

        switch (op)
        {
            case MpccReqOp.Ping:
                RespondOk(op, arg);
                break;
            case MpccReqOp.GetInfo:
                SendStream(op, arg, [MpccInfo.ProtocolVersion, MpccInfo.FwVersionMajor, MpccInfo.FwVersionMinor, _node]);
                break;
            case MpccReqOp.ReadParam:
                if (_params.TryGetValue(arg, out var pv) && FindParam(arg) is { } pd)
                    RespondOk(op, arg, ParamCodec.Encode(pd.Type, pv));
                else
                    Respond(op, arg, StatusCode.ErrBadParam);
                break;
            case MpccReqOp.WriteParam:
                var wpd = FindParam(arg);
                if (wpd is null) { Respond(op, arg, StatusCode.ErrBadParam); break; }
                var value = ParamCodec.Decode(wpd.Type, data);
                if (value < wpd.Min || value > wpd.Max) { Respond(op, arg, StatusCode.ErrOutOfRange); break; }
                _params[arg] = value;
                RespondOk(op, arg);
                break;
            case MpccReqOp.ListSensors:
                SendStream(op, arg, Enum.GetValues<MpccSensor>().SelectMany(s => new byte[] { (byte)s, 0 }).ToArray());
                break;
            case MpccReqOp.ReadSensor:
                HandleReadSensor(arg);
                break;
            case MpccReqOp.SaveConfig:
                RespondOk(op, arg);
                break;
            case MpccReqOp.LoadDefaults:
                foreach (var p in MpccParams.All) _params[p.Id] = p.Default;
                RespondOk(op, arg);
                break;
            case MpccReqOp.GetTime:
                var now = DateTime.Now;
                RespondOk(op, arg, [(byte)now.Second, (byte)now.Minute, (byte)now.Hour, (byte)now.Day, (byte)now.Month, (byte)(now.Year % 100), (byte)((int)now.DayOfWeek + 1)]);
                break;
            case MpccReqOp.SetTime:
                RespondOk(op, arg);
                break;
            case MpccReqOp.GetStatus:
                RespondOk(op, arg, [0xFF, 0x00, 0x07, 0x00, 0x00, 0x00, 0x03]);
                break;
            case MpccReqOp.Reset:
                if (data.Length > 0 && data[0] == 0xA5)
                {
                    RespondOk(op, arg);
                    SendEvent(MpccEventSource.System, MpccEventCode.Boot, [0x08, MpccInfo.ProtocolVersion, MpccInfo.FwVersionMajor, MpccInfo.FwVersionMinor, 0x01, 0x00]);
                }
                else Respond(op, arg, StatusCode.ErrBadParam);
                break;
            case MpccReqOp.GetOutput:
                RespondOk(op, arg, [(byte)(_outputOn ? 1 : 0)]);
                break;
            case MpccReqOp.SetOutput:
                if (data.Length > 0)
                {
                    _outputOn = data[0] != 0;
                    RespondOk(op, arg);
                    SendEvent(MpccEventSource.System, MpccEventCode.OutputChanged, [arg, (byte)(_outputOn ? 1 : 0), 0x00]);
                }
                else Respond(op, arg, StatusCode.ErrBadParam);
                break;
            case MpccReqOp.SetLed:
                RespondOk(op, arg);
                break;
            case MpccReqOp.ReadAdc:
                var mv = 3300 + (int)(200 * Math.Sin(T / 10.0 + arg));
                RespondOk(op, arg, BitConverter.GetBytes(mv));
                break;
            case MpccReqOp.EnterBootloader:
                RespondOk(op, arg); // symulator nie ma bootloadera — akceptuje żądanie, nie resetuje się
                break;
        }
    }

    private void HandleReadSensor(byte sensorArg)
    {
        if (!Enum.IsDefined(typeof(MpccSensor), sensorArg)) { Respond(MpccReqOp.ReadSensor, sensorArg, StatusCode.ErrSensorAbsent); return; }
        var sensor = (MpccSensor)sensorArg;
        var payload = new List<byte>();
        void Add(MpccQuantity q, double value)
        {
            payload.Add((byte)q);
            payload.AddRange(BitConverter.GetBytes((int)Math.Round(value / MpccTables.Quantities[q].Scale)));
        }
        switch (sensor)
        {
            case MpccSensor.Sts31:
                Add(MpccQuantity.Temp, 23.0 + 3.0 * Math.Sin(T / 30.0));
                break;
            case MpccSensor.Mcu:
                Add(MpccQuantity.McuTemp, 32.0 + 2.0 * Math.Sin(T / 35.0));
                Add(MpccQuantity.Vdda, 3.0);
                Add(MpccQuantity.Vbat, 3.05);
                break;
            case MpccSensor.Pcf8574:
                break; // brak wielkości mierzonych
        }
        SendStream(MpccReqOp.ReadSensor, sensorArg, payload.ToArray());
    }

    private ParamDescriptor? FindParam(byte id) => MpccParams.All.FirstOrDefault(p => p.Id == id);

    private void RespondOk(MpccReqOp op, byte arg, ReadOnlySpan<byte> data = default) => Respond(op, arg, StatusCode.Ok, data);
    private void Respond(MpccReqOp op, byte arg, StatusCode status, ReadOnlySpan<byte> data = default) =>
        Send(EnvelopeCodec.BuildResponse(_node, (byte)op, arg, status, data));

    private void SendStream(MpccReqOp op, byte arg, byte[] payload)
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

    private void SendEvent(byte source, MpccEventCode code, ReadOnlySpan<byte> data) =>
        Send(EnvelopeCodec.BuildEvent(_node, source, (byte)code, data));

    private void Send(CanFrame frame) => _ = _ep.SendAsync(frame);

    public void Dispose()
    {
        _timer.Dispose();
        _ep.Dispose();
    }
}
