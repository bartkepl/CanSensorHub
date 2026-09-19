using CanSensorHub.Core.Protocol;

namespace CanSensorHub.Core.Tests;

/// <summary>
/// Układ ładunku telemetrii (8 B) i migawki statusu (7 B) jest identyczny dla każdego
/// modułu. Testy przybijają pozycje bajtów i znaczenie poszczególnych bitów flag —
/// to one decydują, czy wartość pokazana w aplikacji odpowiada temu, co wysłał węzeł.
/// </summary>
public class TelemetryRawTests
{
    [Fact]
    public void Build_uklada_pola_w_wyznaczonych_bajtach()
    {
        var payload = TelemetryRaw.Build(seq: 0x2A, value: -2, nSensors: 3, valid: true, clamped: true, qual: 0x0102);

        Assert.Equal(8, payload.Length);
        Assert.Equal(0x2A, payload[0]);                                   // [0] seq
        Assert.Equal(0b0010_0111, payload[1]);                            // [1] valid + 3 źródła + clamped
        Assert.Equal(new byte[] { 0xFE, 0xFF, 0xFF, 0xFF }, payload[2..6]); // [2:5] i32 LE
        Assert.Equal(new byte[] { 0x02, 0x01 }, payload[6..8]);            // [6:7] u16 LE
    }

    [Fact]
    public void Parse_odwraca_Build()
    {
        var payload = TelemetryRaw.Build(seq: 9, value: 123456, nSensors: 2, valid: true, clamped: false, qual: 950);
        var telemetry = TelemetryRaw.Parse(node: 0x01, channel: 0x03, payload);

        Assert.Equal(0x01, telemetry.Node);
        Assert.Equal(0x03, telemetry.Channel);
        Assert.Equal(9, telemetry.Seq);
        Assert.Equal(123456, telemetry.Value);
        Assert.Equal((ushort)950, telemetry.Qual);
        Assert.True(telemetry.Valid);
        Assert.False(telemetry.Clamped);
        Assert.Equal(2, telemetry.SourceCount);
    }

    [Theory]
    [InlineData(int.MinValue)]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(int.MaxValue)]
    public void Wartosc_przechodzi_przez_kodowanie_bez_zmiany_znaku(int value)
    {
        var payload = TelemetryRaw.Build(0, value, 1, true, false, 0);

        Assert.Equal(value, TelemetryRaw.Parse(0x01, 0x00, payload).Value);
    }

    [Fact]
    public void Flagi_zakresu_sa_niezalezne_od_siebie()
    {
        var warn = TelemetryRaw.Parse(0x01, 0x00,
            TelemetryRaw.Build(0, 0, 1, valid: true, clamped: false, qual: 0, rangeWarn: true));
        var dropped = TelemetryRaw.Parse(0x01, 0x00,
            TelemetryRaw.Build(0, 0, 1, valid: true, clamped: false, qual: 0, rangeErrDropped: true));

        Assert.True(warn.RangeWarn);
        Assert.False(warn.RangeErrDropped);
        Assert.True(dropped.RangeErrDropped);
        Assert.False(dropped.RangeWarn);
    }

    [Fact]
    public void Liczba_zrodel_miesci_sie_w_czterech_bitach()
    {
        // Pole zajmuje bity [4:1]; 15 to maksimum, które nie wchodzi na flagę clamped.
        var payload = TelemetryRaw.Build(0, 0, nSensors: 15, valid: true, clamped: false, qual: 0);
        var telemetry = TelemetryRaw.Parse(0x01, 0x00, payload);

        Assert.Equal(15, telemetry.SourceCount);
        Assert.False(telemetry.Clamped);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(5)]
    [InlineData(7)]
    public void Parse_skroconego_ladunku_nie_wywala_sie(int length)
    {
        // Węzeł ze starszym firmware może nadać krótszą ramkę. Brakujące pola
        // czytamy jako zero, zamiast rzucać wyjątkiem w wątku odbiorczym.
        var telemetry = TelemetryRaw.Parse(0x01, 0x00, new byte[length]);

        Assert.False(telemetry.Valid);
        Assert.Equal(0, telemetry.Value);
        Assert.Equal((ushort)0, telemetry.Qual);
    }
}

public class DeviceStatusReportTests
{
    [Fact]
    public void Parse_czyta_siedmiobajtowy_uklad()
    {
        byte[] payload =
        [
            0x8E,         // [0] SYS
            0x01,         // [1] ERR
            0x03, 0x00,   // [2:3] PRESENT u16 LE
            0x02, 0x00,   // [4:5] FAULT u16 LE
            0x01,         // [6] NHEALTHY
        ];

        var status = DeviceStatusReport.Parse(payload);

        Assert.Equal(0x8E, status.Sys);
        Assert.Equal(0x01, status.Err);
        Assert.Equal((ushort)0x0003, status.Present);
        Assert.Equal((ushort)0x0002, status.Fault);
        Assert.Equal(1, status.NHealthy);
        Assert.True(status.Critical);
    }

    [Fact]
    public void Bitmapy_czytane_sa_jako_little_endian()
    {
        byte[] payload = [0x00, 0x00, 0x34, 0x12, 0x78, 0x56, 0x00];

        var status = DeviceStatusReport.Parse(payload);

        Assert.Equal((ushort)0x1234, status.Present);
        Assert.Equal((ushort)0x5678, status.Fault);
    }

    [Fact]
    public void Flagi_SYS_rozkladaja_sie_na_nazwane_pozycje()
    {
        var status = DeviceStatusReport.Parse([0b1100_0110, 0x00, 0, 0, 0, 0, 0]);

        Assert.True(status.SysFlags.HasFlag(DeviceSysFlags.RtcOk));
        Assert.True(status.SysFlags.HasFlag(DeviceSysFlags.CanOk));
        Assert.True(status.SysFlags.HasFlag(DeviceSysFlags.ConfigOk));
        Assert.True(status.SysFlags.HasFlag(DeviceSysFlags.SensorsOk));
        Assert.False(status.SysFlags.HasFlag(DeviceSysFlags.LseActive));
        Assert.False(status.SysFlags.HasFlag(DeviceSysFlags.AdcOk));
    }

    [Fact]
    public void Flaga_krytyczna_to_bit_zerowy_pola_ERR()
    {
        Assert.True(DeviceStatusReport.Parse([0, 0b0000_0001, 0, 0, 0, 0, 0]).Critical);
        Assert.False(DeviceStatusReport.Parse([0, 0b1111_1110, 0, 0, 0, 0, 0]).Critical);
    }

    [Fact]
    public void Flagi_ERR_rozkladaja_sie_na_nazwane_pozycje()
    {
        var status = DeviceStatusReport.Parse([0x00, 0b0010_1010, 0, 0, 0, 0, 0]);

        Assert.True(status.ErrFlags.HasFlag(DeviceErrFlags.SensorFault));
        Assert.True(status.ErrFlags.HasFlag(DeviceErrFlags.WdtReset));
        Assert.True(status.ErrFlags.HasFlag(DeviceErrFlags.CanBusoff));
        Assert.False(status.ErrFlags.HasFlag(DeviceErrFlags.Critical));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(6)]
    public void Parse_skroconego_ladunku_zwraca_zera_dla_brakujacych_pol(int length)
    {
        var status = DeviceStatusReport.Parse(new byte[length]);

        Assert.Equal(0, status.Sys);
        Assert.Equal((ushort)0, status.Present);
        Assert.Equal(0, status.NHealthy);
    }
}
