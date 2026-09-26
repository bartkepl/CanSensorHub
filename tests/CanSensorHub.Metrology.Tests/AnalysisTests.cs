using CanSensorHub.Metrology.Analysis;
using CanSensorHub.Metrology.Reporting;
using CanSensorHub.Metrology.Simulation;

namespace CanSensorHub.Metrology.Tests;

public class SampleStatsTests
{
    [Fact]
    public void Computes_sample_standard_deviation_and_standard_error()
    {
        var s = SampleStats.From([1.0, 2.0, 3.0, 4.0]);
        Assert.Equal(2.5, s.Mean, 12);
        Assert.Equal(Math.Sqrt(5.0 / 3.0), s.StdDev, 12);
        Assert.Equal(s.StdDev / 2.0, s.StdError, 12);
        Assert.Equal((1.0, 4.0), (s.Min, s.Max));
    }

    [Fact]
    public void Single_sample_has_zero_deviation() => Assert.Equal(0.0, SampleStats.From([5.0]).StdDev);

    [Fact]
    public void Empty_series_is_rejected() => Assert.Throws<ArgumentException>(() => SampleStats.From([]));
}

public class LinearFitTests
{
    [Fact]
    public void Two_points_give_exact_line()
    {
        var f = LinearFit.Fit([1.0, 4.0], [3.0, 9.0]);
        Assert.Equal(2.0, f.Slope, 12);
        Assert.Equal(1.0, f.Intercept, 12);
        Assert.All(f.Residuals, r => Assert.Equal(0.0, r, 12));
    }

    [Fact]
    public void Recovers_small_gain_and_offset_errors_at_microvolt_level()
    {
        // Wartości typowe dla kalibracji: napięcia rzędu woltów, różnice rzędu mikrowoltów.
        double[] x = [0.1, 1.325, 2.55, 3.775, 5.0];
        var y = x.Select(v => 1.000_123 * v - 0.000_456).ToArray();

        var f = LinearFit.Fit(x, y);

        Assert.Equal(1.000_123, f.Slope, 12);
        Assert.Equal(-0.000_456, f.Intercept, 12);
        Assert.True(f.MaxAbsResidual < 1e-12);
        Assert.Equal(1.0, f.RSquared, 12);
    }

    [Fact]
    public void Least_squares_residuals_sum_to_zero()
    {
        var f = LinearFit.Fit([0.0, 1.0, 2.0, 3.0], [0.1, 0.9, 2.2, 2.9]);
        Assert.Equal(0.0, f.Residuals.Sum(), 12);
        Assert.True(f.RSquared is > 0.98 and < 1.0);
    }

    [Fact]
    public void Rejects_degenerate_input()
    {
        Assert.Throws<ArgumentException>(() => LinearFit.Fit([1.0], [1.0]));
        Assert.Throws<ArgumentException>(() => LinearFit.Fit([2.0, 2.0], [1.0, 3.0]));
        Assert.Throws<ArgumentException>(() => LinearFit.Fit([1.0, 2.0], [1.0]));
    }
}

public class SetpointsTests
{
    [Fact]
    public void Linear_includes_both_ends_rounded_to_millivolt()
    {
        Assert.Equal([0.1, 1.325, 2.55, 3.775, 5.0], Setpoints.Linear(0.1, 5.0, 5));
        Assert.Equal([0.333, 0.667], Setpoints.Linear(0.3333, 0.6667, 2));
    }

    [Fact]
    public void Interleaved_points_lie_between_linear_points()
    {
        Assert.Equal([0.5, 1.5, 2.5, 3.5], Setpoints.Interleaved(0.0, 4.0, 4));
    }

    [Fact]
    public void Rejects_invalid_ranges()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Setpoints.Linear(0, 5, 1));
        Assert.Throws<ArgumentException>(() => Setpoints.Linear(5, 0, 3));
        Assert.Throws<ArgumentException>(() => Setpoints.Interleaved(1, 1, 3));
    }
}

public class SimulatedInstrumentTests
{
    [Fact]
    public async Task Source_drives_node_seen_by_voltmeter_and_enforces_limits()
    {
        var node = new AnalogNode();
        var src = new SimulatedVoltageSource(node, 0, 5);
        var dmm = new SimulatedVoltmeter(node, noiseSigmaVolts: 1e-6);

        await src.SetVoltageAsync(2.5);
        var stats = SampleStats.From(await dmm.ReadSamplesAsync(200));

        Assert.Equal(2.5, stats.Mean, 5);
        Assert.InRange(stats.StdDev, 0.5e-6, 2e-6);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => src.SetVoltageAsync(6));

        await src.SetZeroAsync();
        Assert.Equal(0.0, node.Voltage);
    }
}

public class CsvReportWriterTests
{
    [Fact]
    public void Writes_invariant_numbers_and_escapes_separators()
    {
        var path = Path.Combine(Path.GetTempPath(), $"report-{Guid.NewGuid():N}.csv");
        try
        {
            using (var w = new CsvReportWriter(path))
            {
                w.Section("Przyrządy");
                w.KeyValue("DMM", "HEWLETT-PACKARD,34401A,0,11-5-2");
                w.Row("kanał", 1.25, double.NaN, 3);
            }
            var lines = File.ReadAllLines(path);
            Assert.Contains("[Przyrządy]", lines);
            Assert.Contains("DMM,\"HEWLETT-PACKARD,34401A,0,11-5-2\"", lines);
            Assert.Contains("kanał,1.25,,3", lines);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
