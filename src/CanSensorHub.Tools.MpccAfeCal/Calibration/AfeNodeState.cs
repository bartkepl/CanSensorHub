using CanSensorHub.Tools.MpccAfeCal.Node;

namespace CanSensorHub.Tools.MpccAfeCal.Calibration;

/// <summary>Stan kalibracji AFE odczytany z węzła: współczynniki wszystkich kanałów i parametry wspólne.</summary>
public sealed record AfeNodeState(
    IReadOnlyDictionary<int, AfeCoefficients> Coefficients,
    double Tref,
    double MeasurePeriodMs,
    bool CalLocked)
{
    public static async Task<AfeNodeState> ReadAsync(MpccNodeLink node, CancellationToken ct = default)
    {
        var coefficients = new Dictionary<int, AfeCoefficients>();
        foreach (var ch in AfeModel.Channels)
        {
            coefficients[ch.Index] = new AfeCoefficients(
                await node.ReadParamAsync(ch.C0, ct).ConfigureAwait(false),
                await node.ReadParamAsync(ch.C1, ct).ConfigureAwait(false),
                await node.ReadParamAsync(ch.Tc, ct).ConfigureAwait(false));
        }
        return new AfeNodeState(
            coefficients,
            await node.ReadParamAsync(AfeModel.Tref, ct).ConfigureAwait(false),
            await node.ReadParamAsync(AfeModel.MeasurePeriod, ct).ConfigureAwait(false),
            await node.ReadParamAsync(AfeModel.CalLock, ct).ConfigureAwait(false) != 0);
    }
}
