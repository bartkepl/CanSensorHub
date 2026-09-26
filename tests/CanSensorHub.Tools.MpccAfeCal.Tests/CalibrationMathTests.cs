using CanSensorHub.Metrology.Analysis;
using CanSensorHub.Tools.MpccAfeCal.Calibration;

namespace CanSensorHub.Tools.MpccAfeCal.Tests;

public class CalibrationMathTests
{
    /// <summary>Model firmware: V = k·(c1·V_pin + c0).</summary>
    private static double NodeReading(AfeCoefficients c, double pin, double k) => k * (c.C1 * pin + c.C0);

    [Theory]
    [InlineData(0.0, 25.0)]      // bez kompensacji temperaturowej
    [InlineData(0.002, 31.5)]    // kompensacja aktywna, płytka cieplejsza od Tref
    public void Composed_coefficients_make_node_reading_equal_reference(double tc, double temperature)
    {
        const double tref = 25.0;
        var current = new AfeCoefficients(C0: 0.010, C1: 1.6667, Tc: tc);
        var k = AfeCalibrationMath.TemperatureFactor(tc, temperature, tref);

        // Rzeczywisty tor: napięcie wejściowe → pin przez dzielnik z błędem 0.4 % i przesunięciem 5 mV.
        double Pin(double vin) => (vin - 0.005) / (1.6667 * 1.004);
        double[] vin = [0.1, 1.2, 2.4, 3.6, 4.8];
        var fit = LinearFit.Fit(vin.Select(v => NodeReading(current, Pin(v), k)).ToList(), vin);

        var after = AfeCalibrationMath.Compose(current, fit.Slope, fit.Intercept, k);

        Assert.Equal(tc, after.Tc);
        foreach (var v in vin.Append(0.7).Append(4.1))
            Assert.Equal(v, NodeReading(after, Pin(v), k), 9);
    }

    [Fact]
    public void Identity_fit_leaves_coefficients_unchanged()
    {
        var c = new AfeCoefficients(-0.012, 10.02, 0.0);
        Assert.Equal(c, AfeCalibrationMath.Compose(c, 1.0, 0.0, 1.0));
    }

    [Fact]
    public void Temperature_factor_is_one_without_compensation() =>
        Assert.Equal(1.0, AfeCalibrationMath.TemperatureFactor(0.0, 60.0, 25.0));

    [Fact]
    public void Stored_value_is_single_precision() =>
        Assert.Equal((double)(float)1.00012345678, AfeCalibrationMath.AsStored(1.00012345678));

    [Fact]
    public void Saturation_detected_near_adc_full_scale()
    {
        var c = new AfeCoefficients(0.0, 1.6667, 0.0);
        Assert.False(AfeModel.IsSaturated(4.8, c));   // pin ≈ 2.88 V
        Assert.True(AfeModel.IsSaturated(5.0, c));    // pin ≈ 3.00 V
    }

    [Fact]
    public void Channel_table_matches_node_parameter_registry()
    {
        Assert.Equal(7, AfeModel.Channels.Count);
        Assert.Equal(0x30, AfeModel.Channels[0].C0.Id);
        Assert.Equal(0x46, AfeModel.Channels[6].C1.Id);
        Assert.Equal(0x56, AfeModel.Channels[6].Tc.Id);
        Assert.All(AfeModel.Channels, ch => Assert.True(ch.C0.Calibration && ch.C1.Calibration && ch.Tc.Calibration));
        Assert.False(AfeModel.CalLock.Calibration);
        Assert.Equal(0x09, AfeModel.CalLock.Id);
    }
}

public class CalculatorTests
{
    private static PointMeasurement Point(int i, double reference, double node) =>
        new(i, reference, SampleStats.From([reference]),
            Enumerable.Range(0, AfeModel.ChannelCount).Select(_ => (SampleStats?)SampleStats.From([node])).ToList(),
            25.0, DateTimeOffset.Now, 1);

    private static readonly IReadOnlyDictionary<int, AfeCoefficients> Nominal =
        Enumerable.Range(0, AfeModel.ChannelCount).ToDictionary(i => i, i => new AfeCoefficients(0, i < 4 ? 1.6667 : 10.0, 0));

    [Fact]
    public void Rejects_implausible_gain_correction()
    {
        // Węzeł pokazuje połowę napięcia — typowy objaw złego połączenia, nie błędu dzielnika.
        var pts = new[] { Point(0, 1.0, 0.5), Point(1, 4.0, 2.0) };
        var cal = AfeCalibrationCalculator.Compute(pts, Nominal, 25.0, new AfeCalSettings());
        Assert.All(cal, c => Assert.False(c.Accepted));
        Assert.Contains("wzmocnienia", cal[0].Rejection);
    }

    [Fact]
    public void Excludes_saturated_points_from_fit()
    {
        // Trzeci punkt w nasyceniu (pin ≈ 3.0 V na kanałach 0–3) — nie może zaniżyć nachylenia.
        var pts = new[] { Point(0, 1.0, 1.001), Point(1, 3.0, 3.003), Point(2, 5.5, 5.0) };
        var cal = AfeCalibrationCalculator.Compute(pts, Nominal, 25.0, new AfeCalSettings()).Single(c => c.Channel == 0);
        Assert.Equal(1.0 / 1.001, cal.Fit.Slope, 9);
        Assert.Equal(2, cal.Fit.Residuals.Count);
    }

    [Fact]
    public void Check_reports_error_in_millivolts_and_verdict()
    {
        var s = new AfeCalSettings { ToleranceMv = 2.0 };
        var checks = AfeCalibrationCalculator.Evaluate([Point(0, 1.0, 1.0015), Point(1, 2.0, 1.9990)], Nominal, s);
        var ch0 = checks.Single(c => c.Channel == 0);
        Assert.Equal(1.5, ch0.MaxAbsErrorMv, 6);
        Assert.True(ch0.Pass);

        var strict = AfeCalibrationCalculator.Evaluate([Point(0, 1.0, 1.0025)], Nominal, s).Single(c => c.Channel == 0);
        Assert.False(strict.Pass);
    }

    [Fact]
    public void Settings_validation_catches_inconsistent_ranges()
    {
        Assert.Null(new AfeCalSettings().Validate());
        Assert.NotNull(new AfeCalSettings { MinVoltage = 3, MaxVoltage = 2 }.Validate());
        Assert.NotNull(new AfeCalSettings { MaxVoltage = 13 }.Validate());
        Assert.NotNull(new AfeCalSettings { MinVoltage = -1 }.Validate());
        Assert.NotNull(new AfeCalSettings { EnabledChannels = new bool[7] }.Validate());
    }

    [Fact]
    public void Timing_defaults_follow_node_measure_period()
    {
        var (round, settle) = AfeCalibrationProcedure.Timing(new AfeCalSettings(), 1000);
        Assert.Equal(TimeSpan.FromSeconds(1), round);
        Assert.Equal(TimeSpan.FromMilliseconds(2500), settle);

        var (r2, s2) = AfeCalibrationProcedure.Timing(new AfeCalSettings { NodeRoundIntervalMs = 200, SettleMs = 700 }, 1000);
        Assert.Equal((TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(700)), (r2, s2));
    }
}
