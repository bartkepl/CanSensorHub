using System.ComponentModel;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using CanSensorHub.Modules.Mpcc.Protocol;
using CanSensorHub.Modules.Mpcc.ViewModels;

namespace CanSensorHub.Modules.Mpcc.Views;

/// <summary>
/// Code-behind only wires the two ScottPlot charts to the view model's telemetry/sensor streams —
/// ScottPlot's imperative plot-object API doesn't lend itself to pure XAML binding, so this is the one
/// place the module intentionally steps outside strict MVVM. Individual-sensor series are overlaid
/// directly onto the matching averaged-channel plot (Temp or Voltage) rather than a separate plot, mirroring
/// the reference Python app's "one curve per sensor + one bold curve for the average, same axes" layout.
/// </summary>
public partial class MpccDeviceView : UserControl
{
    // Time-based, not count-based: channels/sensors update at very different effective rates (fused
    // telemetry pushed on TELEMETRY_PERIOD vs. per-sensor readings only arriving once a sensor's checkbox
    // is ticked, or on auto-poll), so a fixed sample COUNT gives each series a different rolling time
    // span — the fast ones look dense-and-short, the slow ones stay wide. Trimming by elapsed seconds
    // instead keeps every curve's visible window the same width once trimming kicks in.
    private const double MaxWindowSeconds = 600;
    private readonly Dictionary<MpccChannel, List<double>> _xs = [];
    private readonly Dictionary<MpccChannel, List<double>> _ys = [];

    // Individual-sensor series, keyed by "{Sensor}_{Quantity}" — buffered regardless of checkbox state
    // so toggling a sensor on later still has history to show.
    private readonly Dictionary<string, List<double>> _sensorXs = [];
    private readonly Dictionary<string, List<double>> _sensorYs = [];
    private readonly Dictionary<string, (string SensorKey, string SensorLabel, ChartTarget Target, string QuantityLabel)> _seriesMeta = [];

    private readonly DateTime _start = DateTime.UtcNow;

    // ShowLegend(Edge) creates a new outside-the-data-area legend panel each time it's called rather than
    // replacing the previous one, so it must be invoked at most once per plot — not on every redraw.
    private readonly HashSet<ScottPlot.WPF.WpfPlot> _legendShown = [];

    // Hover-to-inspect: which plotted curves live on each plot right now (rebuilt every redraw, since
    // Plot.Clear() drops the ScottPlot.Plottables.Scatter instances) and one WPF ToolTip per plot that
    // follows the mouse and shows the value of whichever curve's point is nearest the cursor.
    private readonly Dictionary<ScottPlot.WPF.WpfPlot, List<(ScottPlot.Plottables.Scatter Scatter, string Label)>> _hoverSeries = [];
    private readonly Dictionary<ScottPlot.WPF.WpfPlot, ToolTip> _hoverTooltips = [];

    private static readonly MpccChannel[] TempChannels = [MpccChannel.Temp, MpccChannel.McuTemp];
    private static readonly MpccChannel[] VoltageChannels =
        [MpccChannel.Adc0, MpccChannel.Adc1, MpccChannel.Adc2, MpccChannel.Adc3, MpccChannel.Adc4, MpccChannel.Adc5, MpccChannel.Adc6, MpccChannel.Vdda];

    // 16 colors, matching Mpswp's palette — headroom so a plot with many overlaid sensor curves never
    // wraps back to a color already in use on the same plot.
    private static readonly ScottPlot.Color[] Palette =
    [
        ScottPlot.Colors.Red, ScottPlot.Colors.Blue, ScottPlot.Colors.Green, ScottPlot.Colors.Orange,
        ScottPlot.Colors.Purple, ScottPlot.Colors.Brown, ScottPlot.Colors.Cyan, ScottPlot.Colors.Magenta,
        ScottPlot.Colors.Pink, ScottPlot.Colors.Olive, ScottPlot.Colors.Navy, ScottPlot.Colors.Teal,
        ScottPlot.Colors.Gold, ScottPlot.Colors.Indigo, ScottPlot.Colors.Lime, ScottPlot.Colors.Maroon,
    ];

    public MpccDeviceView()
    {
        InitializeComponent();
        foreach (var c in Enum.GetValues<MpccChannel>()) { _xs[c] = []; _ys[c] = []; }

        TempPlot.Plot.Axes.Bottom.Label.Text = "Czas [s] od uruchomienia zakładki";
        TempPlot.Plot.Axes.Left.Label.Text = "Temperatura [°C]";
        VoltagePlot.Plot.Axes.Bottom.Label.Text = "Czas [s] od uruchomienia zakładki";
        VoltagePlot.Plot.Axes.Left.Label.Text = "Napięcie [V]";
        SetupHover(TempPlot);
        SetupHover(VoltagePlot);

        DataContextChanged += (_, e) =>
        {
            if (e.OldValue is MpccDeviceViewModel oldVm)
            {
                oldVm.ChartSampleReceived -= OnSample;
                oldVm.ChartsClearRequested -= OnClearRequested;
                oldVm.SensorChartSampleReceived -= OnSensorSample;
                DetachToggleHandlers(oldVm);
            }
            if (e.NewValue is MpccDeviceViewModel vm)
            {
                vm.ChartSampleReceived += OnSample;
                vm.ChartsClearRequested += OnClearRequested;
                vm.SensorChartSampleReceived += OnSensorSample;
                vm.SensorToggles.CollectionChanged += (_, _) => AttachToggleHandlers(vm);
                AttachToggleHandlers(vm);
            }
        };
    }

    private void AttachToggleHandlers(MpccDeviceViewModel vm)
    {
        foreach (var t in vm.SensorToggles)
        {
            t.PropertyChanged -= OnToggleChanged;
            t.PropertyChanged += OnToggleChanged;
        }
    }

    private void DetachToggleHandlers(MpccDeviceViewModel vm)
    {
        foreach (var t in vm.SensorToggles) t.PropertyChanged -= OnToggleChanged;
    }

    private void OnToggleChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(SensorChartToggleVm.IsChecked)) return;
        Dispatcher.InvokeAsync(() =>
        {
            RedrawTemp();
            RedrawVoltage();
        });
    }

    private void OnSample(object? sender, (MpccChannel Channel, double Value, bool Valid) e)
    {
        if (!e.Valid) return;
        Dispatcher.InvokeAsync(() =>
        {
            var t = (DateTime.UtcNow - _start).TotalSeconds;
            var xs = _xs[e.Channel];
            var ys = _ys[e.Channel];
            xs.Add(t);
            ys.Add(e.Value);
            TrimOld(xs, ys, t);

            if (Array.IndexOf(TempChannels, e.Channel) >= 0) RedrawTemp();
            else if (Array.IndexOf(VoltageChannels, e.Channel) >= 0) RedrawVoltage();
        });
    }

    private void OnSensorSample(object? sender, (string SensorKey, string SensorLabel, ChartTarget Target, string SeriesKey, string QuantityLabel, double Value) e)
    {
        Dispatcher.InvokeAsync(() =>
        {
            var t = (DateTime.UtcNow - _start).TotalSeconds;
            if (!_sensorXs.TryGetValue(e.SeriesKey, out var xs)) { xs = []; _sensorXs[e.SeriesKey] = xs; _sensorYs[e.SeriesKey] = []; }
            _seriesMeta[e.SeriesKey] = (e.SensorKey, e.SensorLabel, e.Target, e.QuantityLabel);
            var ys = _sensorYs[e.SeriesKey];
            xs.Add(t);
            ys.Add(e.Value);
            TrimOld(xs, ys, t);

            if (e.Target == ChartTarget.Temp) RedrawTemp();
            else if (e.Target == ChartTarget.Voltage) RedrawVoltage();
        });
    }

    /// <summary>Drops points older than <see cref="MaxWindowSeconds"/> from the front of a time-ordered
    /// series — same window for every series, so trimming (when it happens) never leaves one curve
    /// covering a shorter span than another.</summary>
    private static void TrimOld(List<double> xs, List<double> ys, double now)
    {
        var cutoff = now - MaxWindowSeconds;
        var i = 0;
        while (i < xs.Count && xs[i] < cutoff) i++;
        if (i == 0) return;
        xs.RemoveRange(0, i);
        ys.RemoveRange(0, i);
    }

    private bool IsSensorChecked(string sensorKey) =>
        DataContext is MpccDeviceViewModel vm && vm.SensorToggles.FirstOrDefault(t => t.Key == sensorKey)?.IsChecked == true;

    private void RedrawTemp() => RedrawTarget(TempPlot, TempChannels, ChartTarget.Temp);
    private void RedrawVoltage() => RedrawTarget(VoltagePlot, VoltageChannels, ChartTarget.Voltage);

    private void RedrawTarget(ScottPlot.WPF.WpfPlot plot, MpccChannel[] channels, ChartTarget target)
    {
        plot.Plot.Clear();
        var hoverList = ResetHoverSeries(plot);
        var colorIdx = 0;

        foreach (var c in channels)
        {
            if (_xs[c].Count < 2) continue;
            var label = MpccTables.Channels[c].Label + " (śr.)";
            var scatter = plot.Plot.Add.Scatter(_xs[c].ToArray(), _ys[c].ToArray());
            scatter.LegendText = label;
            scatter.Color = ScottPlot.Colors.Black;
            scatter.LineWidth = 3;
            hoverList.Add((scatter, label));
        }

        // Plottable series for this target, from checked sensors only.
        var plottable = _seriesMeta
            .Where(kv => kv.Value.Target == target && IsSensorChecked(kv.Value.SensorKey) && _sensorXs[kv.Key].Count >= 2)
            .ToList();
        // A sensor normally contributes one curve per plot, so its name alone is unambiguous — the axis
        // title already says what's measured. Only append the quantity when the SAME sensor puts more
        // than one curve on this SAME plot (e.g. MCU: VDDA + VBAT both land on "Napięcia ADC").
        var seriesPerSensor = plottable.CountBy(kv => kv.Value.SensorKey).ToDictionary(g => g.Key, g => g.Value);

        foreach (var (seriesKey, (sensorKey, sensorLabel, _, quantityLabel)) in plottable)
        {
            var xs = _sensorXs[seriesKey];
            var ys = _sensorYs[seriesKey];
            var label = seriesPerSensor[sensorKey] > 1 ? $"{sensorLabel} {quantityLabel}" : sensorLabel;
            var scatter = plot.Plot.Add.Scatter(xs.ToArray(), ys.ToArray());
            scatter.LegendText = label;
            scatter.Color = Palette[colorIdx++ % Palette.Length];
            hoverList.Add((scatter, label));
        }

        plot.Plot.Axes.AutoScale();
        if (_legendShown.Add(plot)) plot.Plot.ShowLegend(ScottPlot.Edge.Right);
        plot.Refresh();
    }

    private List<(ScottPlot.Plottables.Scatter Scatter, string Label)> ResetHoverSeries(ScottPlot.WPF.WpfPlot plot)
    {
        if (!_hoverSeries.TryGetValue(plot, out var list)) { list = []; _hoverSeries[plot] = list; }
        else list.Clear();
        return list;
    }

    private void SetupHover(ScottPlot.WPF.WpfPlot plot)
    {
        _hoverTooltips[plot] = new ToolTip { Placement = PlacementMode.Relative, PlacementTarget = plot };
        plot.MouseMove += OnPlotMouseMove;
        plot.MouseLeave += (_, _) => _hoverTooltips[plot].IsOpen = false;
    }

    private void OnPlotMouseMove(object? sender, MouseEventArgs e)
    {
        if (sender is not ScottPlot.WPF.WpfPlot plot || !_hoverTooltips.TryGetValue(plot, out var tooltip)) return;
        var pos = e.GetPosition(plot);
        var mousePixel = new ScottPlot.Pixel(pos.X, pos.Y);

        if (!_hoverSeries.TryGetValue(plot, out var series) || series.Count == 0) { tooltip.IsOpen = false; return; }

        var mouseCoord = plot.Plot.GetCoordinates(mousePixel);
        var bestDist = double.MaxValue;
        ScottPlot.DataPoint best = ScottPlot.DataPoint.None;
        string? bestLabel = null;
        foreach (var (scatter, label) in series)
        {
            var nearest = scatter.Data.GetNearest(mouseCoord, plot.Plot.LastRender, 15f);
            if (!nearest.IsReal) continue;
            var nearestPixel = plot.Plot.GetPixel(new ScottPlot.Coordinates(nearest.X, nearest.Y));
            var dx = nearestPixel.X - mousePixel.X;
            var dy = nearestPixel.Y - mousePixel.Y;
            var dist = dx * dx + dy * dy;
            if (dist >= bestDist) continue;
            bestDist = dist;
            best = nearest;
            bestLabel = label;
        }

        if (bestLabel is null) { tooltip.IsOpen = false; return; }

        // best.X is seconds elapsed since _start, so the sample's actual wall-clock moment is always
        // reconstructible from it — no need to track a parallel timestamp per point.
        var sampleTime = _start.AddSeconds(best.X).ToLocalTime();
        tooltip.Content = $"{bestLabel}\n{best.Y:0.###}\n{sampleTime:HH:mm:ss}  (t={best.X:0.#} s)";
        tooltip.HorizontalOffset = pos.X + 14;
        tooltip.VerticalOffset = pos.Y + 14;
        tooltip.IsOpen = true;
    }

    private void OnClearRequested(object? sender, EventArgs e)
    {
        foreach (var c in Enum.GetValues<MpccChannel>()) { _xs[c].Clear(); _ys[c].Clear(); }
        foreach (var key in _sensorXs.Keys) { _sensorXs[key].Clear(); _sensorYs[key].Clear(); }
        Dispatcher.InvokeAsync(() =>
        {
            foreach (var plot in new[] { TempPlot, VoltagePlot })
            {
                plot.Plot.Clear();
                plot.Refresh();
            }
        });
    }
}
