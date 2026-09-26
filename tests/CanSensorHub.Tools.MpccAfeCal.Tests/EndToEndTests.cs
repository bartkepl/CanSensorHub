using CanSensorHub.Core.Can;
using CanSensorHub.Core.Protocol;
using CanSensorHub.Metrology.Analysis;
using CanSensorHub.Modules.Mpcc.Protocol;
using CanSensorHub.Tools.MpccAfeCal.Calibration;
using CanSensorHub.Tools.MpccAfeCal.Reporting;

namespace CanSensorHub.Tools.MpccAfeCal.Tests;

/// <summary>Pełny przebieg na stanowisku symulowanym: węzeł MPCC ma dzielniki z błędem rzędu 0.5 %.</summary>
public class EndToEndTests
{
    private static int SaveConfigRequests(CanBusService bus) =>
        bus.HistorySnapshot.Count(e =>
            e.Direction == FrameDirection.Tx &&
            CanId.Unpack(e.Frame.Id) is { Func: (byte)CanFunc.Request, Obj: (byte)MpccReqOp.SaveConfig });

    [Fact]
    public async Task Calibration_brings_all_channels_within_tolerance_and_relocks_node()
    {
        await using var bench = await SimulatedBench.StartAsync();
        var s = SimulatedBench.FastSettings();
        var ct = CancellationToken.None;

        var before = await AfeNodeState.ReadAsync(bench.Link, ct);
        Assert.True(before.CalLocked);

        // Przed kalibracją błędy dzielników dają kilkanaście mV na kanałach 5 V i więcej na 30 V.
        var checkSetpoints = Setpoints.Interleaved(s.MinVoltage, s.MaxVoltage, s.CheckPoints);
        var preCheck = AfeCalibrationCalculator.Evaluate(await bench.Procedure(s).MeasureAsync(checkSetpoints, null, ct), before.Coefficients, s);
        Assert.All(preCheck, c => Assert.False(c.Pass));

        var points = await bench.Procedure(s).MeasureAsync(Setpoints.Linear(s.MinVoltage, s.MaxVoltage, s.CalibrationPoints), null, ct);
        Assert.Equal(0.0, bench.Bus.SimulatedBus!.AnalogInput.Voltage);   // zadajnik wrócił do 0 V

        var cal = AfeCalibrationCalculator.Compute(points, before.Coefficients, before.Tref, s);
        Assert.Equal(7, cal.Count);
        Assert.All(cal, c => Assert.True(c.Accepted, c.Rejection));

        var outcome = await new MpccCalibrationWriter(bench.Link).WriteAsync(cal, ct);
        Assert.True(outcome.Success, string.Join(Environment.NewLine, outcome.Log));
        Assert.Equal(1, SaveConfigRequests(bench.Bus));

        var after = await AfeNodeState.ReadAsync(bench.Link, ct);
        Assert.True(after.CalLocked);
        Assert.All(cal, c => Assert.Equal(c.After, after.Coefficients[c.Channel]));

        var check = AfeCalibrationCalculator.Evaluate(await bench.Procedure(s).MeasureAsync(checkSetpoints, null, ct), after.Coefficients, s);
        Assert.All(check, c => Assert.True(c.Pass, $"CH{c.Channel}: {c.MaxAbsErrorMv:0.00} mV"));
    }

    [Fact]
    public async Task Two_point_mode_also_corrects_gain_and_offset()
    {
        await using var bench = await SimulatedBench.StartAsync();
        var s = SimulatedBench.FastSettings(points: 2);
        var ct = CancellationToken.None;
        var before = await AfeNodeState.ReadAsync(bench.Link, ct);

        var points = await bench.Procedure(s).MeasureAsync(Setpoints.Linear(s.MinVoltage, s.MaxVoltage, 2), null, ct);
        var cal = AfeCalibrationCalculator.Compute(points, before.Coefficients, before.Tref, s);
        Assert.True((await new MpccCalibrationWriter(bench.Link).WriteAsync(cal, ct)).Success);

        var after = await AfeNodeState.ReadAsync(bench.Link, ct);
        var check = AfeCalibrationCalculator.Evaluate(
            await bench.Procedure(s).MeasureAsync(Setpoints.Interleaved(s.MinVoltage, s.MaxVoltage, 4), null, ct), after.Coefficients, s);
        Assert.All(check, c => Assert.True(c.Pass, $"CH{c.Channel}: {c.MaxAbsErrorMv:0.00} mV"));
    }

    [Fact]
    public async Task Locked_node_rejects_calibration_write_directly()
    {
        await using var bench = await SimulatedBench.StartAsync();
        var status = await bench.Link.WriteParamAsync(AfeModel.Channels[0].C1, 1.7, CancellationToken.None);
        Assert.Equal(StatusCode.ErrReadonly, status);
    }

    [Fact]
    public async Task Failed_write_restores_previous_coefficients_and_lock_without_saving()
    {
        await using var bench = await SimulatedBench.StartAsync();
        var ct = CancellationToken.None;
        var before = await AfeNodeState.ReadAsync(bench.Link, ct);

        var fit = LinearFit.Fit([1.0, 2.0], [1.0, 2.0]);
        var good = new ChannelCalibration(0, before.Coefficients[0], before.Coefficients[0] with { C1 = 1.7, C0 = 0.01 }, fit, 1.0, null);
        // c1 poza zakresem parametru (0..1000) — węzeł odrzuci zapis statusem ERR_OUT_OF_RANGE.
        var bad = new ChannelCalibration(1, before.Coefficients[1], before.Coefficients[1] with { C1 = 5000 }, fit, 1.0, null);

        var outcome = await new MpccCalibrationWriter(bench.Link).WriteAsync([good, bad], ct);

        Assert.False(outcome.Success);
        Assert.Equal(0, SaveConfigRequests(bench.Bus));
        var after = await AfeNodeState.ReadAsync(bench.Link, ct);
        Assert.True(after.CalLocked);
        Assert.Equal(before.Coefficients[0], after.Coefficients[0]);
        Assert.Equal(before.Coefficients[1], after.Coefficients[1]);
    }

    [Fact]
    public async Task Cancelled_measurement_leaves_source_at_zero()
    {
        await using var bench = await SimulatedBench.StartAsync();
        using var cts = new CancellationTokenSource();
        var s = SimulatedBench.FastSettings();
        var proc = new AfeCalibrationProcedure(bench.Source, bench.Voltmeter, bench.Link, s,
            nodeRoundInterval: TimeSpan.FromMilliseconds(20), settleTime: TimeSpan.FromMilliseconds(50));

        var run = proc.MeasureAsync(Setpoints.Linear(0.1, 4.8, 5), null, cts.Token);
        await Task.Delay(120, CancellationToken.None);
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        Assert.Equal(0.0, bench.Bus.SimulatedBus!.AnalogInput.Voltage);
    }

    [Fact]
    public async Task Report_contains_raw_points_and_results()
    {
        await using var bench = await SimulatedBench.StartAsync();
        var ct = CancellationToken.None;
        var s = SimulatedBench.FastSettings(points: 3, checkPoints: 2);
        var state = await AfeNodeState.ReadAsync(bench.Link, ct);
        var session = new AfeCalibrationSession { NodeId = SimulatedBench.Node, Settings = s, Simulated = true, CoefficientsBefore = state.Coefficients };
        session.Node = await bench.Link.GetInfoAsync(ct);
        session.CalibrationPoints = await bench.Procedure(s).MeasureAsync(Setpoints.Linear(0.1, 4.8, 3), null, ct);
        session.Calibrations = AfeCalibrationCalculator.Compute(session.CalibrationPoints, state.Coefficients, state.Tref, s);

        var path = Path.Combine(Path.GetTempPath(), AfeCalibrationReport.DefaultFileName(session));
        try
        {
            AfeCalibrationReport.Write(path, session);
            var text = File.ReadAllText(path);
            Assert.Contains("[Punkty kalibracji]", text);
            Assert.Contains("[Dopasowanie]", text);
            Assert.Contains("CH6_sigma_V", text);
            Assert.StartsWith("MPCC-AFE_", Path.GetFileName(path));
            Assert.Contains(session.Node.UidHex!, path);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
