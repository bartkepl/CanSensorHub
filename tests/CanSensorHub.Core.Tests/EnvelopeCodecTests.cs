using CanSensorHub.Core.Protocol;

namespace CanSensorHub.Core.Tests;

/// <summary>
/// Otoczka FUNC/NODE/OBJ/ARG jest wspólna dla wszystkich modułów — buduje ją i czyta
/// zarówno monitor magistrali, jak i typowane warstwy MPCC/MPSWP. Testy sprawdzają
/// dokładny układ bajtów w ładunku, bo to on jest kontraktem z firmware.
/// </summary>
public class EnvelopeCodecTests
{
    [Fact]
    public void BuildRequest_ustawia_FUNC_na_REQUEST_i_przenosi_dane()
    {
        var frame = EnvelopeCodec.BuildRequest(node: 0x10, opcode: 0x21, arg: 0x03, data: [0xAA, 0xBB]);
        var id = CanId.Unpack(frame.Id);

        Assert.True(frame.IsExtended);
        Assert.Equal((byte)CanFunc.Request, id.Func);
        Assert.Equal(0x10, id.Node);
        Assert.Equal(0x21, id.Obj);
        Assert.Equal(0x03, id.Arg);
        Assert.Equal(new byte[] { 0xAA, 0xBB }, frame.Data);
    }

    [Fact]
    public void ParseRequest_odwraca_BuildRequest()
    {
        var frame = EnvelopeCodec.BuildRequest(0x10, 0x21, 0x03, [0x01, 0x02, 0x03]);
        var request = EnvelopeCodec.ParseRequest(CanId.Unpack(frame.Id), frame.Data);

        Assert.Equal(0x10, request.Node);
        Assert.Equal(0x21, request.Opcode);
        Assert.Equal(0x03, request.Arg);
        Assert.Equal(new byte[] { 0x01, 0x02, 0x03 }, request.Data);
    }

    [Fact]
    public void BuildResponse_wstawia_status_jako_bajt_zerowy()
    {
        var frame = EnvelopeCodec.BuildResponse(0x10, 0x21, 0x00, StatusCode.ErrOutOfRange, [0x7F]);

        Assert.Equal((byte)StatusCode.ErrOutOfRange, frame.Data[0]);
        Assert.Equal(0x7F, frame.Data[1]);
    }

    [Fact]
    public void ParseResponse_oddziela_status_od_danych()
    {
        var frame = EnvelopeCodec.BuildResponse(0x10, 0x21, 0x05, StatusCode.Ok, [0x11, 0x22]);
        var response = EnvelopeCodec.ParseResponse(CanId.Unpack(frame.Id), frame.Data);

        Assert.Equal(StatusCode.Ok, response.Status);
        Assert.Equal(0x21, response.Opcode);
        Assert.Equal(0x05, response.Arg);
        Assert.Equal(new byte[] { 0x11, 0x22 }, response.Data);
    }

    [Fact]
    public void ParseResponse_pustej_ramki_nie_wywala_sie()
    {
        // Węzeł może odpowiedzieć ramką o zerowej długości. Interpretujemy jako OK
        // bez danych, zamiast rzucać wyjątkiem w wątku odbiorczym magistrali.
        var response = EnvelopeCodec.ParseResponse(new CanId(0x09, 0x10, 0x21, 0x00), []);

        Assert.Equal(StatusCode.Ok, response.Status);
        Assert.Empty(response.Data);
    }

    [Fact]
    public void ParseResponse_samego_statusu_zwraca_puste_dane()
    {
        var response = EnvelopeCodec.ParseResponse(new CanId(0x09, 0x10, 0x21, 0x00), [(byte)StatusCode.ErrBusy]);

        Assert.Equal(StatusCode.ErrBusy, response.Status);
        Assert.Empty(response.Data);
    }

    [Theory]
    [InlineData(0, false, 0x00)]
    [InlineData(0, true, 0x80)]
    [InlineData(3, false, 0x03)]
    [InlineData(3, true, 0x83)]
    [InlineData(127, true, 0xFF)]
    public void Naglowek_segmentu_laczy_indeks_z_bitem_konca(int index, bool last, byte expected)
    {
        Assert.Equal(expected, StreamHeaderBits.Build(index, last));
    }

    [Fact]
    public void BuildStream_i_ParseStream_zachowuja_indeks_flage_i_ladunek()
    {
        var frame = EnvelopeCodec.BuildStream(0x01, 0x40, 0x00, index: 2, last: true, payload: [0xDE, 0xAD]);
        var segment = EnvelopeCodec.ParseStream(CanId.Unpack(frame.Id), frame.Data);

        Assert.Equal(2, segment.Index);
        Assert.True(segment.Last);
        Assert.Equal(new byte[] { 0xDE, 0xAD }, segment.Payload);
        Assert.Equal(0x01, segment.Node);
        Assert.Equal(0x40, segment.Obj);
    }

    [Fact]
    public void ParseStream_segmentu_bez_ladunku_zwraca_pusta_tablice()
    {
        var frame = EnvelopeCodec.BuildStream(0x01, 0x40, 0x00, index: 0, last: true, payload: []);
        var segment = EnvelopeCodec.ParseStream(CanId.Unpack(frame.Id), frame.Data);

        Assert.Empty(segment.Payload);
        Assert.True(segment.Last);
    }

    [Fact]
    public void BuildEvent_mapuje_zrodlo_na_OBJ_a_kod_na_ARG()
    {
        var frame = EnvelopeCodec.BuildEvent(0x01, source: 0x02, code: 0x7B, data: [0x01]);
        var evt = EnvelopeCodec.ParseEvent(CanId.Unpack(frame.Id), frame.Data);

        Assert.Equal((byte)CanFunc.Event, CanId.Unpack(frame.Id).Func);
        Assert.Equal(0x02, evt.Source);
        Assert.Equal(0x7B, evt.Code);
    }

    [Fact]
    public void BuildTelemetry_zeruje_ARG_i_umieszcza_kanal_w_OBJ()
    {
        var payload = TelemetryRaw.Build(seq: 7, value: -1234, nSensors: 2, valid: true, clamped: false, qual: 900);
        var frame = EnvelopeCodec.BuildTelemetry(node: 0x01, channel: 0x05, payload);
        var id = CanId.Unpack(frame.Id);

        Assert.Equal((byte)CanFunc.Telemetry, id.Func);
        Assert.Equal(0x05, id.Obj);
        Assert.Equal(0x00, id.Arg);

        var telemetry = EnvelopeCodec.ParseTelemetry(id, frame.Data);
        Assert.Equal(0x05, telemetry.Channel);
        Assert.Equal(-1234, telemetry.Value);
    }
}
