using CanSensorHub.Metrology.Instruments;
using CanSensorHub.Tools.MpccAfeCal.Calibration;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CanSensorHub.Tools.MpccAfeCal.ViewModels;

/// <summary>Adres VISA z rozpoznanym przyrządem (po <c>*IDN?</c>).</summary>
public sealed record VisaResourceItem(string Address, InstrumentIdentity? Identity, string? Error)
{
    public string Label => Identity is { } id ? $"{id.Model} — {Address}" : Error is null ? Address : $"{Address} (brak odpowiedzi)";
    public override string ToString() => Label;
}

public sealed record ModeOption(CalibrationMode Mode, string Label);

/// <summary>Pole wyboru kanału do kalibracji.</summary>
public sealed partial class ChannelOptionVm(AfeChannelInfo info, bool enabled) : ObservableObject
{
    public int Index => info.Index;
    public string Label => $"{info.Name} (0…{info.NominalFullScaleVolts:0} V)";
    [ObservableProperty] private bool _enabled = enabled;
}

/// <summary>Wiersz tabeli współczynników: stan węzła, wynik dopasowania i wynik sprawdzenia jednego kanału.</summary>
public sealed partial class ChannelRowVm(int channel) : ObservableObject
{
    public int Channel { get; } = channel;
    public string Name => $"CH{Channel}";

    [ObservableProperty] private double? _c0Before;
    [ObservableProperty] private double? _c1Before;
    [ObservableProperty] private double? _tc;
    [ObservableProperty] private double? _gain;
    [ObservableProperty] private double? _offsetMv;
    [ObservableProperty] private double? _maxResidualMv;
    [ObservableProperty] private double? _c0After;
    [ObservableProperty] private double? _c1After;
    [ObservableProperty] private string? _calibrationStatus;
    [ObservableProperty] private bool _calibrationRejected;
    [ObservableProperty] private double? _checkMaxErrorMv;
    [ObservableProperty] private string? _checkVerdict;
    [ObservableProperty] private bool? _checkPass;

    public void ClearCalibration()
    {
        Gain = OffsetMv = MaxResidualMv = C0After = C1After = null;
        CalibrationStatus = null;
        CalibrationRejected = false;
    }

    public void ClearCheck()
    {
        CheckMaxErrorMv = null;
        CheckVerdict = null;
        CheckPass = null;
    }
}

/// <summary>Wiersz tabeli punktów: wzorzec i błąd każdego kanału względem wzorca.</summary>
public sealed class PointRowVm
{
    public required string Phase { get; init; }
    public required int Number { get; init; }
    public required double Setpoint { get; init; }
    public required double Reference { get; init; }
    public required double ReferenceSigmaMv { get; init; }
    public double? Temperature { get; init; }
    public int Attempts { get; init; }
    public required IReadOnlyList<double?> ErrorMv { get; init; }

    public double? E0 => ErrorMv[0];
    public double? E1 => ErrorMv[1];
    public double? E2 => ErrorMv[2];
    public double? E3 => ErrorMv[3];
    public double? E4 => ErrorMv[4];
    public double? E5 => ErrorMv[5];
    public double? E6 => ErrorMv[6];

    public static PointRowVm From(string phase, PointMeasurement p) => new()
    {
        Phase = phase,
        Number = p.Index + 1,
        Setpoint = p.Setpoint,
        Reference = p.Reference.Mean,
        ReferenceSigmaMv = p.Reference.StdDev * 1000.0,
        Temperature = p.BoardTemperature,
        Attempts = p.Attempts,
        ErrorMv = p.Channels.Select(c => c is { } s ? (s.Mean - p.Reference.Mean) * 1000.0 : (double?)null).ToList(),
    };
}

public sealed record LogLineVm(DateTime Time, ProcedureMessageKind Kind, string Text)
{
    public bool IsWarning => Kind == ProcedureMessageKind.Warning;
    public bool IsError => Kind == ProcedureMessageKind.Error;
}

/// <summary>Seria wykresu błędu: jeden kanał w jednej fazie (przed kalibracją albo w sprawdzeniu).</summary>
public sealed record ErrorSeries(int Channel, bool AfterCalibration, IReadOnlyList<(double Reference, double ErrorMv)> Points);
