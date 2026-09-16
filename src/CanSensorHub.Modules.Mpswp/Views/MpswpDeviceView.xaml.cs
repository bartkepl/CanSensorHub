using System.ComponentModel;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using CanSensorHub.Modules.Mpswp.Protocol;
using CanSensorHub.Modules.Mpswp.ViewModels;

namespace CanSensorHub.Modules.Mpswp.Views;

/// <summary>
/// Code-behind only wires the four ScottPlot charts to the view model's telemetry/sensor streams —
/// ScottPlot's imperative plot-object API doesn't lend itself to pure XAML binding, so this is the one
/// place the module intentionally steps outside strict MVVM. Individual-sensor series are overlaid
/// directly onto the matching averaged-channel plot (Temp/Hum/Press/Air) rather than a separate plot,
/// mirroring the reference Python app's "one curve per sensor + one bold curve for the average, same axes" layout.
/// </summary>
public partial class MpswpDeviceView : UserControl
{
    // Time-based, not count-based: channels/sensors update at very different effective rates (fused
    // telemetry pushed on TELEMETRY_PERIOD vs. per-sensor readings only arriving once a sensor's checkbox
    // is ticked, or on auto-poll), so a fixed sample COUNT gives each series a different rolling time
    // span — the fast ones look dense-and-short, the slow ones stay wide. Trimming by elapsed seconds
    // instead keeps every curve's visible window the same width once trimming kicks in.
    private const double MaxWindowSeconds = 600;
    private readonly Dictionary<MpswpChannel, List<double>> _xs = [];
    private readonly Dictionary<MpswpChannel, List<double>> _ys = [];

    // Individual-sensor series, keyed by "{Sensor}_{Quantity}" — buffered regardless of checkbox state
    // so toggling a sensor on later still has history to show.
    private readonly Dictionary<string, List<double>> _sensorXs = [];
    private readonly Dictionary<string, List<double>> _sensorYs = [];
    private readonly Dictionary<string, (string SensorKey, string SensorLabel, ChartTarget Target, string QuantityLabel)> _seriesMeta = [];

    // AS3935 lightning strikes: sparse, discrete events — plotted as time-vs-distance points (not a
    // connected line, which would imply a trend between unrelated strikes) with energy shown via color
    // tercile instead of a shared axis, since raw/energy and distance[km] are wildly different scales.
    private readonly List<(double TimeSeconds, int DistanceKm, int Energy)> _lightningStrikes = [];
    private const int MaxLightningPoints = 300;

    private readonly DateTime _start = DateTime.UtcNow;

    // ShowLegend(Edge) creates a new outside-the-data-area legend panel each time it's called rather than
    // replacing the previous one, so it must be invoked at most once per plot — not on every redraw.
    private readonly HashSet<ScottPlot.WPF.WpfPlot> _legendShown = [];

    // Hover-to-inspect: which plotted curves live on each plot right now (rebuilt every redraw, since
    // Plot.Clear() drops the ScottPlot.Plottables.Scatter instances) and one WPF ToolTip per plot that
    // follows the mouse and shows the value of whichever curve's point is nearest the cursor.
    private readonly Dictionary<ScottPlot.WPF.WpfPlot, List<(ScottPlot.Plottables.Scatter Scatter, string Label)>> _hoverSeries = [];
    private readonly Dictionary<ScottPlot.WPF.WpfPlot, ToolTip> _hoverTooltips = [];

    private static readonly MpswpChannel[] AirChannels = [MpswpChannel.VocIndex, MpswpChannel.NoxIndex, MpswpChannel.Iaq];

    // 16 colors: the Temp plot alone can carry 9 series (8 sensors + MCU internal ADC), which used to
    // wrap past the old 8-color palette and silently reuse a color (MCU landed back on Red, same as
    // HTU21D) — two unrelated curves became visually indistinguishable.
    private static readonly ScottPlot.Color[] Palette =
    [
        ScottPlot.Colors.Red, ScottPlot.Colors.Blue, ScottPlot.Colors.Green, ScottPlot.Colors.Orange,
        ScottPlot.Colors.Purple, ScottPlot.Colors.Brown, ScottPlot.Colors.Cyan, ScottPlot.Colors.Magenta,
        ScottPlot.Colors.Pink, ScottPlot.Colors.Olive, ScottPlot.Colors.Navy, ScottPlot.Colors.Teal,
        ScottPlot.Colors.Gold, ScottPlot.Colors.Indigo, ScottPlot.Colors.Lime, ScottPlot.Colors.Maroon,
    ];

    public MpswpDeviceView()
    {
        InitializeComponent();
        foreach (var c in Enum.GetValues<MpswpChannel>()) { _xs[c] = []; _ys[c] = []; }

        const string timeAxisLabel = "Czas [s] od uruchomienia zakładki";
        TempPlot.Plot.Axes.Bottom.Label.Text = timeAxisLabel;
        TempPlot.Plot.Axes.Left.Label.Text = "Temperatura [°C]";
        HumPlot.Plot.Axes.Bottom.Label.Text = timeAxisLabel;
        HumPlot.Plot.Axes.Left.Label.Text = "Wilgotność [%RH]";
        PressPlot.Plot.Axes.Bottom.Label.Text = timeAxisLabel;
        PressPlot.Plot.Axes.Left.Label.Text = "Ciśnienie [Pa]";
        AirPlot.Plot.Axes.Bottom.Label.Text = timeAxisLabel;
        AirPlot.Plot.Axes.Left.Label.Text = "Indeks jakości powietrza [0–500]";
        LightningPlot.Plot.Axes.Bottom.Label.Text = timeAxisLabel;
        LightningPlot.Plot.Axes.Left.Label.Text = "Odległość wyładowania [km]";
        foreach (var plot in new[] { TempPlot, HumPlot, PressPlot, AirPlot, LightningPlot }) SetupHover(plot);

        DataContextChanged += (_, e) =>
        {
            if (e.OldValue is MpswpDeviceViewModel oldVm)
            {
                oldVm.ChartSampleReceived -= OnSample;
                oldVm.ChartsClearRequested -= OnClearRequested;
                oldVm.SensorChartSampleReceived -= OnSensorSample;
                oldVm.LightningStrikeReceived -= OnLightningStrike;
                DetachToggleHandlers(oldVm);
            }
            if (e.NewValue is MpswpDeviceViewModel vm)
            {
                vm.ChartSampleReceived += OnSample;
                vm.ChartsClearRequested += OnClearRequested;
                vm.SensorChartSampleReceived += OnSensorSample;
                vm.LightningStrikeReceived += OnLightningStrike;
                vm.SensorToggles.CollectionChanged += (_, _) => AttachToggleHandlers(vm);
                AttachToggleHandlers(vm);
            }
        };
    }

    private void AttachToggleHandlers(MpswpDeviceViewModel vm)
    {
        foreach (var t in vm.SensorToggles)
        {
            t.PropertyChanged -= OnToggleChanged;
            t.PropertyChanged += OnToggleChanged;
        }
    }

    private void DetachToggleHandlers(MpswpDeviceViewModel vm)
    {
        foreach (var t in vm.SensorToggles) t.PropertyChanged -= OnToggleChanged;
    }

    private void OnToggleChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(SensorChartToggleVm.IsChecked)) return;
        Dispatcher.InvokeAsync(() =>
        {
            RedrawTarget(TempPlot, ChartTarget.Temp);
            RedrawTarget(HumPlot, ChartTarget.Hum);
            RedrawTarget(PressPlot, ChartTarget.Press);
            RedrawTarget(AirPlot, ChartTarget.Air);
        });
    }

    private void OnSample(object? sender, (MpswpChannel Channel, double Value, bool Valid) e)
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

            switch (e.Channel)
            {
                case MpswpChannel.Temp: RedrawTarget(TempPlot, ChartTarget.Temp); break;
                case MpswpChannel.Hum: RedrawTarget(HumPlot, ChartTarget.Hum); break;
                case MpswpChannel.Press: RedrawTarget(PressPlot, ChartTarget.Press); break;
                default:
                    if (Array.IndexOf(AirChannels, e.Channel) >= 0) RedrawTarget(AirPlot, ChartTarget.Air);
                    break;
            }
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

            var plot = e.Target switch
            {
                ChartTarget.Temp => TempPlot,
                ChartTarget.Hum => HumPlot,
                ChartTarget.Press => PressPlot,
                ChartTarget.Air => AirPlot,
                _ => null,
            };
            if (plot is not null) RedrawTarget(plot, e.Target);
        });
    }

    private void OnLightningStrike(object? sender, (int DistanceKm, int Energy) e)
    {
        Dispatcher.InvokeAsync(() =>
        {
            var t = (DateTime.UtcNow - _start).TotalSeconds;
            _lightningStrikes.Add((t, e.DistanceKm, e.Energy));
            if (_lightningStrikes.Count > MaxLightningPoints) _lightningStrikes.RemoveAt(0);
            RedrawLightning();
        });
    }

    private void RedrawLightning()
    {
        LightningPlot.Plot.Clear();
        var hoverList = ResetHoverSeries(LightningPlot);
        if (_lightningStrikes.Count == 0)
        {
            LightningPlot.Plot.Axes.SetLimits(0, 60, 0, 40);
            LightningPlot.Refresh();
            return;
        }

        // Bucket strikes into relative energy terciles (AS3935's "energy" register has no fixed physical
        // unit, so a fixed threshold would be meaningless) — color conveys "weak/medium/strong" without
        // needing a second numeric axis that would fight the distance scale.
        var minE = _lightningStrikes.Min(s => s.Energy);
        var maxE = _lightningStrikes.Max(s => s.Energy);
        var span = Math.Max(maxE - minE, 1);

        var low = _lightningStrikes.Where(s => (double)(s.Energy - minE) / span < 1.0 / 3).ToList();
        var mid = _lightningStrikes.Where(s => (double)(s.Energy - minE) / span is >= 1.0 / 3 and < 2.0 / 3).ToList();
        var high = _lightningStrikes.Where(s => (double)(s.Energy - minE) / span >= 2.0 / 3).ToList();

        void AddBucket(List<(double TimeSeconds, int DistanceKm, int Energy)> bucket, string label, ScottPlot.Color color)
        {
            if (bucket.Count == 0) return;
            var scatter = LightningPlot.Plot.Add.Scatter(
                bucket.Select(s => s.TimeSeconds).ToArray(),
                bucket.Select(s => (double)s.DistanceKm).ToArray());
            scatter.LegendText = label;
            scatter.Color = color;
            scatter.LineWidth = 0; // punkty bez łączącej linii — to odrębne, niepowiązane zdarzenia
            scatter.MarkerSize = 12;
            hoverList.Add((scatter, label));
        }

        // Yellow had barely any contrast against a light background — swapped for a low→high energy
        // progression (blue/orange/red) that reads clearly on both light and dark theme.
        AddBucket(low, "Energia: niska", ScottPlot.Colors.Blue);
        AddBucket(mid, "Energia: średnia", ScottPlot.Colors.Orange);
        AddBucket(high, "Energia: wysoka", ScottPlot.Colors.Red);

        LightningPlot.Plot.Axes.AutoScale();
        if (_legendShown.Add(LightningPlot)) LightningPlot.Plot.ShowLegend(ScottPlot.Edge.Right);
        LightningPlot.Refresh();
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
        DataContext is MpswpDeviceViewModel vm && vm.SensorToggles.FirstOrDefault(t => t.Key == sensorKey)?.IsChecked == true;

    private IEnumerable<MpswpChannel> ChannelsFor(ChartTarget target) => target switch
    {
        ChartTarget.Temp => [MpswpChannel.Temp],
        ChartTarget.Hum => [MpswpChannel.Hum],
        ChartTarget.Press => [MpswpChannel.Press],
        ChartTarget.Air => AirChannels,
        _ => [],
    };

    private void RedrawTarget(ScottPlot.WPF.WpfPlot plot, ChartTarget target)
    {
        plot.Plot.Clear();
        var hoverList = ResetHoverSeries(plot);
        var colorIdx = 0;

        foreach (var c in ChannelsFor(target))
        {
            if (_xs[c].Count < 2) continue;
            var label = MpswpTables.Channels[c].Label + " (śr.)";
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
        // than one curve on this SAME plot (e.g. BME680: temperature + humidity both land on... no,
        // those are different plots; this mainly matters for MCU: VDDA + VBAT sharing one target).
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

        // best.X is seconds elapsed since _start, so the sample's (or lightning strike's) actual
        // wall-clock moment is always reconstructible from it — no need for a parallel timestamp per point.
        var sampleTime = _start.AddSeconds(best.X).ToLocalTime();
        tooltip.Content = $"{bestLabel}\n{best.Y:0.###}\n{sampleTime:HH:mm:ss}  (t={best.X:0.#} s)";
        tooltip.HorizontalOffset = pos.X + 14;
        tooltip.VerticalOffset = pos.Y + 14;
        tooltip.IsOpen = true;
    }

    private void OnClearRequested(object? sender, EventArgs e)
    {
        foreach (var c in Enum.GetValues<MpswpChannel>()) { _xs[c].Clear(); _ys[c].Clear(); }
        foreach (var key in _sensorXs.Keys) { _sensorXs[key].Clear(); _sensorYs[key].Clear(); }
        _lightningStrikes.Clear();
        Dispatcher.InvokeAsync(() =>
        {
            foreach (var plot in new[] { TempPlot, HumPlot, PressPlot, AirPlot, LightningPlot })
            {
                plot.Plot.Clear();
                plot.Refresh();
            }
        });
    }
}
