using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CanSensorHub.Core.Protocol;

namespace CanSensorHub.Modules.Mpcc.ViewModels;

/// <summary>One dashboard tile: a live telemetry channel value.</summary>
public partial class ChannelTileVm : ObservableObject
{
    public required string Name { get; init; }
    public required string Unit { get; init; }
    [ObservableProperty] private double _value;
    [ObservableProperty] private bool _isValid;
    [ObservableProperty] private DateTimeOffset? _lastUpdate;

    // DisplayValue is a plain computed property (not [ObservableProperty]) — without these hooks, WPF
    // never learns it changed when Value/IsValid do, and the tile freezes at its initial "—" forever.
    partial void OnValueChanged(double value) => OnPropertyChanged(nameof(DisplayValue));
    partial void OnIsValidChanged(bool value) => OnPropertyChanged(nameof(DisplayValue));

    public string DisplayValue => IsValid ? Value.ToString(Unit == "°C" || Unit == "V" ? "F3" : "F2") : "—";
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
    /// <summary>Byte-address-like params (NODE_ID) shown/edited as hex ("0x10") instead of decimal.</summary>
    public bool IsHex => IsNumeric && Descriptor.DisplayHex;
    /// <summary>Everything else: f32 calibration coefficients get decimals, integer types (periods, counts...) show as plain whole numbers.</summary>
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

/// <summary>One row of the Sensors tab — reads itself independently of the others via <see cref="ReadCommand"/>.</summary>
public partial class SensorRowVm(string name, Func<Task> onRead) : ObservableObject
{
    public string Name { get; } = name;
    [ObservableProperty] private string _status = "?";
    [ObservableProperty] private string _lastReading = "—";
    [ObservableProperty] private bool _isReading;
    [ObservableProperty] private DateTimeOffset? _lastUpdate;

    public bool IsPresent => Status == "obecny";

    partial void OnStatusChanged(string value) => OnPropertyChanged(nameof(IsPresent));

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
/// One curve on the Charts tab — either an averaged telemetry channel or a single quantity of a single
/// sensor. The same instance backs the side-panel checkbox and the plot's legend entry, so hiding a
/// curve from either place is reflected in the other.
/// </summary>
public partial class ChartSeriesVm : ObservableObject
{
    public required string Key { get; init; }
    /// <summary>Label under the group in the side panel — the group header already names the sensor.</summary>
    public required string Label { get; init; }
    /// <summary>Label in the plot legend and hover tooltip — must stand alone, so it names the source too.</summary>
    public required string LegendLabel { get; init; }
    public required ChartTarget Target { get; init; }
    public required string ColorHex { get; init; }
    public bool IsAverage { get; init; }
    [ObservableProperty] private bool _isChecked;
    /// <summary>Legend entries stay hidden until the curve has at least one sample — a sensor that is
    /// never polled would otherwise clutter the legend with entries that toggle nothing.</summary>
    [ObservableProperty] private bool _hasData;

    [RelayCommand]
    private void Toggle() => IsChecked = !IsChecked;
}

/// <summary>
/// Side-panel group of <see cref="ChartSeriesVm"/> — one per sensor plus one for the averaged channels.
/// The header checkbox is three-state: null when only some of the group's curves are shown.
/// </summary>
public sealed class ChartSeriesGroupVm : ObservableObject
{
    public string Label { get; }
    public IReadOnlyList<ChartSeriesVm> Series { get; }

    public ChartSeriesGroupVm(string label, IReadOnlyList<ChartSeriesVm> series)
    {
        Label = label;
        Series = series;
        foreach (var s in series)
        {
            s.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(ChartSeriesVm.IsChecked)) OnPropertyChanged(nameof(IsChecked));
            };
        }
    }

    public bool? IsChecked
    {
        get => Series.All(s => s.IsChecked) ? true : Series.Any(s => s.IsChecked) ? null : false;
        set
        {
            // A two-state CheckBox never writes null back (it cycles indeterminate -> checked), so null
            // here can only come from a programmatic write and carries no intent.
            if (value is not { } on) return;
            foreach (var s in Series) s.IsChecked = on;
        }
    }
}

/// <summary>One row of the Bus Monitor / Response log used by module tabs that show raw traffic context.</summary>
public sealed record ResponseLogRowVm(DateTimeOffset Timestamp, string Opcode, string Status, string DataHex);
