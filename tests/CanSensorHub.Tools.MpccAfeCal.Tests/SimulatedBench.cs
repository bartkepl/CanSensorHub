using CanSensorHub.Core.Can;
using CanSensorHub.Metrology.Simulation;
using CanSensorHub.Modules.Mpcc;
using CanSensorHub.Tools.MpccAfeCal.Calibration;
using CanSensorHub.Tools.MpccAfeCal.Node;

namespace CanSensorHub.Tools.MpccAfeCal.Tests;

/// <summary>Adapter wejścia analogowego magistrali symulowanej na punkt połączeniowy przyrządów symulowanych.</summary>
internal sealed class BusAnalogNode(SimulatedAnalogInput input) : IAnalogNode
{
    public double? Voltage { get => input.Voltage; set => input.Voltage = value; }
}

/// <summary>Kompletne stanowisko symulowane: magistrala, węzeł MPCC, zadajnik i woltomierz na wspólnym wejściu.</summary>
internal sealed class SimulatedBench : IAsyncDisposable
{
    public const byte Node = 0x10;

    public CanBusService Bus { get; } = new();
    public MpccSimulator Simulator { get; private set; } = null!;
    public MpccNodeLink Link { get; private set; } = null!;
    public SimulatedVoltageSource Source { get; private set; } = null!;
    public SimulatedVoltmeter Voltmeter { get; private set; } = null!;

    public static async Task<SimulatedBench> StartAsync(double maxVoltage = 4.8)
    {
        var b = new SimulatedBench();
        await b.Bus.ConnectSimulatedAsync();
        b.Simulator = new MpccSimulator(b.Bus.SimulatedBus!, Node);
        b.Link = new MpccNodeLink(b.Bus, Node);
        var analog = new BusAnalogNode(b.Bus.SimulatedBus!.AnalogInput);
        b.Source = new SimulatedVoltageSource(analog, 0, maxVoltage);
        b.Voltmeter = new SimulatedVoltmeter(analog, noiseSigmaVolts: 5e-6);
        return b;
    }

    /// <summary>Ustawienia skrócone czasowo: symulator odpowiada natychmiast i każdy odczyt ma świeży szum.</summary>
    public static AfeCalSettings FastSettings(int points = 5, int checkPoints = 6) => new()
    {
        MinVoltage = 0.1,
        MaxVoltage = 4.8,
        Mode = points == 2 ? CalibrationMode.TwoPoint : CalibrationMode.MultiPoint,
        CalibrationPoints = points,
        CheckPoints = checkPoints,
        DmmSamples = 10,
        NodeRounds = 20,
        ToleranceMv = 5.0,
    };

    public AfeCalibrationProcedure Procedure(AfeCalSettings s) =>
        new(Source, Voltmeter, Link, s, nodeRoundInterval: TimeSpan.Zero, settleTime: TimeSpan.Zero);

    public ValueTask DisposeAsync()
    {
        Link.Dispose();
        Simulator.Dispose();
        Bus.Dispose();
        return ValueTask.CompletedTask;
    }
}
