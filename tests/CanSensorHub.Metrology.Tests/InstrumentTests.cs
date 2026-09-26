using CanSensorHub.Metrology.Instruments;
using CanSensorHub.Metrology.Visa;

namespace CanSensorHub.Metrology.Tests;

public class InstrumentIdentityTests
{
    [Theory]
    [InlineData("HEWLETT-PACKARD,34401A,0,11-5-2", InstrumentKind.Dmm34401A)]
    [InlineData("Agilent Technologies,34970A,0,13-2-2", InstrumentKind.Daq34970A)]
    [InlineData("Keysight Technologies,34972A,MY123,1.0", InstrumentKind.Daq34970A)]
    [InlineData("KEITHLEY INSTRUMENTS INC.,MODEL 2000,123,A01", InstrumentKind.Unknown)]
    public void Recognizes_supported_models(string idn, InstrumentKind expected) =>
        Assert.Equal(expected, InstrumentIdentity.Parse(idn).Kind);

    [Fact]
    public void Parses_fields_and_tolerates_missing_ones()
    {
        var id = InstrumentIdentity.Parse("HEWLETT-PACKARD, 34401A ,0,11-5-2\n");
        Assert.Equal(("HEWLETT-PACKARD", "34401A", "0", "11-5-2"), (id.Manufacturer, id.Model, id.Serial, id.Firmware));

        var partial = InstrumentIdentity.Parse("ACME");
        Assert.Equal(("ACME", "", ""), (partial.Manufacturer, partial.Model, partial.Firmware));
    }
}

public class Dmm34401ATests
{
    [Fact]
    public async Task Configure_sends_fixed_range_nplc_autozero_and_high_impedance()
    {
        var s = new RecordingScpiSession();
        await new Dmm34401A(s, new Dmm34401ASettings { RangeVolts = 10, Nplc = 10, AutoZero = true, HighImpedance = true }).ConfigureAsync();

        Assert.Equal(
            ["*CLS", "CONF:VOLT:DC 10", "VOLT:DC:NPLC 10", "ZERO:AUTO ON", "INP:IMP:AUTO ON", "TRIG:SOUR IMM", "TRIG:DEL:AUTO ON", "SYST:ERR?"],
            s.Commands);
    }

    [Fact]
    public async Task Configure_rejects_values_the_instrument_does_not_support()
    {
        var s = new RecordingScpiSession();
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => new Dmm34401A(s, new Dmm34401ASettings { Nplc = 5 }).ConfigureAsync());
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => new Dmm34401A(s, new Dmm34401ASettings { RangeVolts = 5 }).ConfigureAsync());
        Assert.Empty(s.Commands);
    }

    [Fact]
    public async Task Configure_surfaces_instrument_error_queue()
    {
        var s = new RecordingScpiSession().Respond("SYST:ERR?", "-222,\"Data out of range\"", "+0,\"No error\"");
        var ex = await Assert.ThrowsAsync<InstrumentException>(() => new Dmm34401A(s, new Dmm34401ASettings()).ConfigureAsync());
        Assert.Contains("Data out of range", ex.Message);
    }

    [Fact]
    public async Task Reads_whole_series_with_one_trigger_and_scaled_timeout()
    {
        var s = new RecordingScpiSession().Respond("READ?", "+2.50001230E+00,+2.50001180E+00,+2.50001250E+00");
        var dmm = new Dmm34401A(s, new Dmm34401ASettings { Nplc = 10, AutoZero = true });

        var values = await dmm.ReadSamplesAsync(3);

        Assert.Equal(["SAMP:COUN 3", "READ?"], s.Commands);
        Assert.Equal([2.5000123, 2.5000118, 2.5000125], values);
        // 3 próbki × (10/50 s × 2 za autozero) = 1.2 s czystego całkowania; limit musi być większy.
        Assert.True(s.QueryTimeouts[0] > TimeSpan.FromSeconds(1.2));
    }

    [Fact]
    public async Task Rejects_response_with_wrong_sample_count()
    {
        var s = new RecordingScpiSession().Respond("READ?", "+1.0E+00,+1.0E+00");
        await Assert.ThrowsAsync<InstrumentException>(() => new Dmm34401A(s, new Dmm34401ASettings()).ReadSamplesAsync(3));
    }
}

public class Daq34970ATests
{
    [Theory]
    [InlineData(100, 1, 104)]
    [InlineData(200, 2, 205)]
    [InlineData(300, 1, 304)]
    public void Maps_dac_outputs_to_channels_s04_s05(int slot, int dac, int channel) =>
        Assert.Equal(channel, Daq34970A.DacChannel(slot, dac));

    [Theory]
    [InlineData(150, 1)]
    [InlineData(200, 3)]
    public void Rejects_invalid_dac_address(int slot, int dac) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => Daq34970A.DacChannel(slot, dac));

    [Fact]
    public async Task Sets_dac_with_millivolt_resolution_and_checks_error_queue()
    {
        var s = new RecordingScpiSession();
        await new Daq34970A(s).SetDacAsync(200, 1, 2.34567);
        Assert.Equal(["SOUR:VOLT 2.346,(@204)", "SYST:ERR?"], s.Commands);
    }

    [Fact]
    public async Task Refuses_voltage_beyond_dac_range_without_sending()
    {
        var s = new RecordingScpiSession();
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => new Daq34970A(s).SetDacAsync(100, 1, 12.5));
        Assert.Empty(s.Commands);
    }

    [Fact]
    public async Task Reports_card_models_with_empty_slots_blank()
    {
        var s = new RecordingScpiSession()
            .Respond("SYST:CTYP? 100", "HEWLETT-PACKARD,34901A,0,1.0")
            .Respond("SYST:CTYP? 200", "HEWLETT-PACKARD,34907A,0,1.0")
            .Respond("SYST:CTYP? 300", "HEWLETT-PACKARD,0,0,0");

        var cards = await new Daq34970A(s).GetCardModelsAsync();

        Assert.Equal("34901A", cards[100]);
        Assert.Equal("34907A", cards[200]);
        Assert.Equal("", cards[300]);
    }

    [Fact]
    public async Task Channel_enforces_operator_limit_and_zeroes_on_request()
    {
        var s = new RecordingScpiSession();
        var ch = new Dac34907AChannel(new Daq34970A(s), 200, 2, operatorMin: 0, operatorMax: 5);

        Assert.Equal((0.0, 5.0), (ch.MinVoltage, ch.MaxVoltage));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => ch.SetVoltageAsync(5.2));
        Assert.Empty(s.Commands);

        await ch.SetVoltageAsync(5.0);
        await ch.SetZeroAsync();
        Assert.Equal(["SOUR:VOLT 5.000,(@205)", "SYST:ERR?", "SOUR:VOLT 0.000,(@205)", "SYST:ERR?"], s.Commands);
    }

    [Fact]
    public void Channel_limit_never_exceeds_dac_hardware_range()
    {
        var ch = new Dac34907AChannel(new Daq34970A(new RecordingScpiSession()), 100, 1, -20, 20);
        Assert.Equal((-12.0, 12.0), (ch.MinVoltage, ch.MaxVoltage));
    }
}
