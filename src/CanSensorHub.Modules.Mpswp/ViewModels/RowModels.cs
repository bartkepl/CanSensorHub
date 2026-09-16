using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CanSensorHub.Core.Protocol;
using CanSensorHub.Modules.Mpswp.Protocol;

namespace CanSensorHub.Modules.Mpswp.ViewModels;

/// <summary>One dashboard tile: a live telemetry channel value.</summary>
public partial class ChannelTileVm : ObservableObject
{
    public required string Name { get; init; }
    public required string Unit { get; init; }
    [ObservableProperty] private double _value;
    [ObservableProperty] private bool _isValid;
    [ObservableProperty] private DateTimeOffset? _lastUpdate;
    /// <summary>At least one contributing sensor is outside LPL/UPL (process range) — value still fused in.</summary>
    [ObservableProperty] private bool _rangeWarn;
    /// <summary>At least one contributing sensor was excluded from fusion (outside its LTL/UTL transducer range).</summary>
    [ObservableProperty] private bool _rangeErrDropped;

    // DisplayValue is a plain computed property (not [ObservableProperty]) — without these hooks, WPF
    // never learns it changed when Value/IsValid do, and the tile freezes at its initial "—" forever.
    partial void OnValueChanged(double value) => OnPropertyChanged(nameof(DisplayValue));
    partial void OnIsValidChanged(bool value) => OnPropertyChanged(nameof(DisplayValue));
    partial void OnRangeWarnChanged(bool value) { OnPropertyChanged(nameof(RangeStatusText)); OnPropertyChanged(nameof(HasRangeIssue)); }
    partial void OnRangeErrDroppedChanged(bool value) { OnPropertyChanged(nameof(RangeStatusText)); OnPropertyChanged(nameof(HasRangeIssue)); }

    public string DisplayValue => IsValid ? Value.ToString(Unit == "Pa" || Unit == "idx" ? "F0" : "F2") : "—";

    public bool HasRangeIssue => RangeWarn || RangeErrDropped;

    public string RangeStatusText => RangeErrDropped
        ? "⚠ czujnik odrzucony (poza LTL/UTL)"
        : RangeWarn
            ? "⚠ poza zakresem podstawowym (LPL/UPL)"
            : "";
}

/// <summary>One editable row in the Params tab, wrapping a <see cref="ParamDescriptor"/> with live/pending values.</summary>
public partial class ParamEditRowVm(ParamDescriptor descriptor, Func<ParamDescriptor, double, Task> onWrite) : ObservableObject
{
    public ParamDescriptor Descriptor { get; } = descriptor;
    public string Name => Descriptor.Name;
    public string Description => Descriptor.Description;
    public string Unit => Descriptor.Unit;
    public double Min => Descriptor.Min;
    public double Max => Descriptor.Max;
    public bool IsReadOnly => Descriptor.IsReadOnly;
    public ParamControlKind Control => Descriptor.Control;
    public IReadOnlyList<ParamOption>? Options => Descriptor.Options;
    public int DecimalPlaces => Descriptor.DecimalPlaces;
    public bool IsCheckbox => Control == ParamControlKind.Checkbox;
    public bool IsCombo => Control == ParamControlKind.ComboBox;
    public bool IsNumeric => Control == ParamControlKind.Numeric;
    /// <summary>Byte-address-like params (NODE_ID) shown/edited as hex ("0x01") instead of decimal.</summary>
    public bool IsHex => IsNumeric && Descriptor.DisplayHex;
    /// <summary>Everything else: f32 calibration coefficients get decimals, integer types (periods, counts, weights...) show as plain whole numbers.</summary>
    public bool IsPlainNumeric => IsNumeric && !IsHex;
    private bool IsFloatType => Descriptor.Type == ParamValueType.F32;

    [ObservableProperty] private double _value = descriptor.Default;
    [ObservableProperty] private bool _hasPendingRead;
    [ObservableProperty] private DateTimeOffset? _lastSynced;

    // BoolValue/ComboValue/NumericText/HexText are plain computed views over Value; re-notify them
    // whenever Value changes (e.g. when a device READ_PARAM response arrives) so the bound
    // CheckBox/ComboBox/TextBox stay in sync.
    partial void OnValueChanged(double value)
    {
        OnPropertyChanged(nameof(BoolValue));
        OnPropertyChanged(nameof(ComboValue));
        OnPropertyChanged(nameof(NumericText));
        OnPropertyChanged(nameof(HexText));
    }

    public bool BoolValue
    {
        get => Value != 0;
        set => Value = value ? 1 : 0;
    }

    public int ComboValue
    {
        get => (int)Value;
        set => Value = value;
    }

    public string NumericText
    {
        get => IsFloatType
            ? Value.ToString("F" + Math.Max(DecimalPlaces, 1), CultureInfo.InvariantCulture)
            : ((long)Math.Round(Value)).ToString(CultureInfo.InvariantCulture);
        set
        {
            if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
                Value = Math.Clamp(parsed, Min, Max);
        }
    }

    public string HexText
    {
        get => $"0x{(long)Math.Round(Value):X2}";
        set
        {
            var text = value.Trim();
            if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) text = text[2..];
            if (long.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var parsed))
                Value = Math.Clamp(parsed, Min, Max);
        }
    }

    public void ApplyDeviceValue(double v)
    {
        Value = v;
        LastSynced = DateTimeOffset.Now;
    }

    [RelayCommand]
    private async Task Write() => await onWrite(Descriptor, Value);
}

/// <summary>
/// One reading line in a Sensors-tab card: the value itself (with a ⚠/✗ marker when out of range) plus,
/// for anything flagged, a dim "zakres: lo–hi" hint showing the exact LPL/UPL or LTL/UTL bound it was
/// judged against — otherwise a WARN/ERR marker is just an accusation with no evidence.
/// </summary>
public sealed record SensorReadingDisplayVm(string ValueText, string? LimitText, bool IsWarning, bool IsRejected)
{
    public bool HasLimitText => LimitText is not null;
}

/// <summary>One row of the Sensors tab — reads itself independently of the others via <see cref="ReadCommand"/>.</summary>
public partial class SensorRowVm(string name, MpswpSensor sensor, Func<Task> onRead) : ObservableObject
{
    // Per-quantity status of this sensor's most recent readings (from READ_SENSOR and/or EVENT
    // SENSOR_RANGE) — a multi-quantity sensor like BME680 needs all of them to know whether ANY
    // quantity is currently out of range, not just the last one touched.
    private readonly Dictionary<MpswpQuantity, MpswpReadingStatus> _quantityStatus = [];

    public string Name { get; } = name;
    public ObservableCollection<SensorReadingDisplayVm> Readings { get; } = [];
    [ObservableProperty] private string _status = "?";
    [ObservableProperty] private bool _isReading;
    [ObservableProperty] private DateTimeOffset? _lastUpdate;
    /// <summary>Set when any quantity is outside LTL/UTL (excluded from fusion) — takes priority over <see cref="HasRangeWarning"/>.</summary>
    [ObservableProperty] private bool _hasRangeError;
    /// <summary>Set when any quantity is outside LPL/UPL (still fused, just implausible).</summary>
    [ObservableProperty] private bool _hasRangeWarning;

    public bool IsPresent => Status == "obecny";
    public bool HasReadings => Readings.Count > 0;
    public bool HasNoReadings => !HasReadings;

    partial void OnStatusChanged(string value) => OnPropertyChanged(nameof(IsPresent));

    /// <summary>Applies a full READ_SENSOR result: replaces the known status of every reported quantity and rebuilds <see cref="Readings"/>.</summary>
    public void ApplyReadings(IReadOnlyList<MpswpSensorReading> readings)
    {
        _quantityStatus.Clear();
        foreach (var r in readings) _quantityStatus[r.Quantity] = r.Status;
        RecomputeRangeFlags();

        Readings.Clear();
        foreach (var r in readings) Readings.Add(BuildDisplay(r));
        OnPropertyChanged(nameof(HasReadings));
        OnPropertyChanged(nameof(HasNoReadings));

        LastUpdate = DateTimeOffset.Now;
    }

    /// <summary>Applies one EVENT SENSOR_RANGE edge, ahead of the next full READ_SENSOR poll.</summary>
    public void ApplyRangeEvent(MpswpQuantity quantity, MpswpReadingStatus status)
    {
        _quantityStatus[quantity] = status;
        RecomputeRangeFlags();
    }

    private void RecomputeRangeFlags()
    {
        HasRangeError = _quantityStatus.Values.Any(s => s is MpswpReadingStatus.ErrLow or MpswpReadingStatus.ErrHigh);
        HasRangeWarning = !HasRangeError && _quantityStatus.Values.Any(s => s is MpswpReadingStatus.WarnLow or MpswpReadingStatus.WarnHigh);
    }

    private SensorReadingDisplayVm BuildDisplay(MpswpSensorReading r)
    {
        string? limitText = null;
        if (r.Status != MpswpReadingStatus.Ok && MpswpLimits.TryGetRelevantRange(sensor, r.Quantity, r.Status, out var lo, out var hi))
            limitText = $"zakres: {FormatNumber(lo, r.Unit)}–{FormatNumber(hi, r.Unit)} {r.Unit}";
        return new SensorReadingDisplayVm(FormatReading(r), limitText, r.IsWarning, r.IsRejected);
    }

    private static string FormatReading(MpswpSensorReading r)
    {
        var value = FormatNumber(r.Value, r.Unit);
        return r.Status switch
        {
            MpswpReadingStatus.WarnLow or MpswpReadingStatus.WarnHigh => $"{value} {r.Unit} ⚠",
            MpswpReadingStatus.ErrLow or MpswpReadingStatus.ErrHigh => $"{value} {r.Unit} ✗ odrzucone",
            _ => $"{value} {r.Unit}",
        };
    }

    // Same F0/F2 heuristic as ChannelTileVm.DisplayValue — whole numbers for Pa/idx, two decimals otherwise.
    private static string FormatNumber(double v, string unit) => v.ToString(unit is "Pa" or "idx" ? "F0" : "F2", CultureInfo.InvariantCulture);

    [RelayCommand]
    private async Task Read()
    {
        IsReading = true;
        try { await onRead(); }
        finally { IsReading = false; }
    }
}

/// <summary>One row of the Events log.</summary>
public sealed record EventLogRowVm(DateTimeOffset Timestamp, string Source, string Code, string DataHex, string Description);

/// <summary>One read-only checkbox-style indicator on the Status tab (one SYS or ERR flag).</summary>
public partial class FlagIndicatorVm : ObservableObject
{
    public required string Name { get; init; }
    public required string Description { get; init; }
    public bool IsDanger { get; init; }
    [ObservableProperty] private bool _isSet;
}

/// <summary>
/// One checkbox on the Charts tab per SENSOR (not per measured quantity) — checking e.g. "SHT45" plots
/// every quantity that sensor provides (temperature AND humidity together), each overlaid on its
/// matching existing averaged-channel plot, exactly like the reference Python app's per-sensor curves.
/// </summary>
public partial class SensorChartToggleVm : ObservableObject
{
    public required string Key { get; init; }
    public required string Label { get; init; }
    [ObservableProperty] private bool _isChecked;
}
