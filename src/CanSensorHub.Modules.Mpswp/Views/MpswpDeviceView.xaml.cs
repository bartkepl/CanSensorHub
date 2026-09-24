using System.ComponentModel;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using CanSensorHub.Modules.Mpswp.ViewModels;

namespace CanSensorHub.Modules.Mpswp.Views;

/// <summary>
/// Code-behind only wires the ScottPlot charts to the view model's chart samples — ScottPlot's
/// imperative plot-object API doesn't lend itself to pure XAML binding, so this is the one place the
/// module intentionally steps outside strict MVVM. Individual-sensor series are overlaid onto the
/// matching averaged-channel plot (Temp/Hum/Press/Air). Which curves exist, their colors and whether
/// they are shown all come from <see cref="ChartSeriesVm"/>; the legend of those four plots is plain WPF
/// bound to the same objects (ScottPlot's own legend is a bitmap and can't be clicked).
/// </summary>
public partial class MpswpDeviceView : UserControl
{
    // Time-based, not count-based: channels/sensors update at very different effective rates (fused
    // telemetry pushed on TELEMETRY_PERIOD vs. per-sensor readings only arriving on demand or on
    // auto-poll), so a fixed sample COUNT gives each series a different rolling time
    // span — the fast ones look dense-and-short, the slow ones stay wide. Trimming by elapsed seconds
    // instead keeps every curve's visible window the same width once trimming kicks in.
    private const double MaxWindowSeconds = 600;

    // Keyed by ChartSeriesVm.Key — buffered regardless of visibility so switching a curve on later still
    // has history to show.
    private readonly Dictionary<string, (List<double> Xs, List<double> Ys)> _buffers = [];

    // AS3935 lightning strikes: sparse, discrete events — plotted as time-vs-distance points (not a
    // connected line, which would imply a trend between unrelated strikes) with energy shown via color
    // tercile instead of a shared axis, since raw/energy and distance[km] are wildly different scales.
    private readonly List<(double TimeSeconds, int DistanceKm, int Energy)> _lightningStrikes = [];
    private const int MaxLightningPoints = 300;

    private readonly DateTime _start = DateTime.UtcNow;

    // Only the lightning plot keeps ScottPlot's legend (energy classes, nothing to toggle). ShowLegend(Edge)
    // creates a new outside-the-data-area legend panel on every call, so it must run once, not per redraw.
    private bool _lightningLegendShown;

    // Hover-to-inspect: which plotted curves live on each plot right now (rebuilt every redraw, since
    // Plot.Clear() drops the ScottPlot.Plottables.Scatter instances) and one WPF ToolTip per plot that
    // follows the mouse and shows the value of whichever curve's point is nearest the cursor.
    private readonly Dictionary<ScottPlot.WPF.WpfPlot, List<(ScottPlot.Plottables.Scatter Scatter, string Label)>> _hoverSeries = [];
    private readonly Dictionary<ScottPlot.WPF.WpfPlot, ToolTip> _hoverTooltips = [];

    public MpswpDeviceView()
    {
        InitializeComponent();

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
                oldVm.LightningStrikeReceived -= OnLightningStrike;
                foreach (var s in oldVm.ChartSeriesGroups.SelectMany(g => g.Series)) s.PropertyChanged -= OnSeriesChanged;
            }
            if (e.NewValue is MpswpDeviceViewModel vm)
            {
                vm.ChartSampleReceived += OnSample;
                vm.ChartsClearRequested += OnClearRequested;
                vm.LightningStrikeReceived += OnLightningStrike;
                foreach (var s in vm.ChartSeriesGroups.SelectMany(g => g.Series)) s.PropertyChanged += OnSeriesChanged;
            }
        };
    }

    private void OnSeriesChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ChartSeriesVm.IsChecked) || sender is not ChartSeriesVm series) return;
        Dispatcher.InvokeAsync(() => Redraw(series.Target));
    }

    private void OnSample(object? sender, (string SeriesKey, double Value) e)
    {
        Dispatcher.InvokeAsync(() =>
        {
            if (DataContext is not MpswpDeviceViewModel vm || vm.FindChartSeries(e.SeriesKey) is not { } series) return;
            var t = (DateTime.UtcNow - _start).TotalSeconds;
            if (!_buffers.TryGetValue(e.SeriesKey, out var buf)) { buf = ([], []); _buffers[e.SeriesKey] = buf; }
            buf.Xs.Add(t);
            buf.Ys.Add(e.Value);
            TrimOld(buf.Xs, buf.Ys, t);
            if (series.IsChecked) Redraw(series.Target);
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
        if (!_lightningLegendShown)
        {
            LightningPlot.Plot.ShowLegend(ScottPlot.Edge.Right);
            _lightningLegendShown = true;
        }
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

    private void Redraw(ChartTarget target)
    {
        if (DataContext is not MpswpDeviceViewModel vm) return;
        var plot = target switch
        {
            ChartTarget.Temp => TempPlot,
            ChartTarget.Hum => HumPlot,
            ChartTarget.Press => PressPlot,
            ChartTarget.Air => AirPlot,
            _ => null,
        };
        if (plot is not null) RedrawPlot(plot, vm.ChartSeriesFor(target));
    }

    private void RedrawPlot(ScottPlot.WPF.WpfPlot plot, IReadOnlyList<ChartSeriesVm> seriesList)
    {
        plot.Plot.Clear();
        var hoverList = ResetHoverSeries(plot);

        foreach (var series in seriesList)
        {
            if (!series.IsChecked || !_buffers.TryGetValue(series.Key, out var buf) || buf.Xs.Count == 0) continue;
            var scatter = plot.Plot.Add.Scatter(buf.Xs.ToArray(), buf.Ys.ToArray());
            scatter.Color = ScottPlot.Color.FromHex(series.ColorHex);
            // Averages: bold line without markers — the continuous reference. Sensors: thin line with a
            // marker per sample, since polled readings are sparse. The legend swatch in the XAML mirrors this.
            if (series.IsAverage)
            {
                scatter.LineWidth = 3;
                scatter.MarkerSize = 0;
            }
            else
            {
                scatter.LineWidth = 1.5f;
                scatter.MarkerSize = 5;
            }
            hoverList.Add((scatter, series.LegendLabel));
        }

        plot.Plot.Axes.AutoScale();
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
        Dispatcher.InvokeAsync(() =>
        {
            _buffers.Clear();
            _lightningStrikes.Clear();
            foreach (var plot in new[] { TempPlot, HumPlot, PressPlot, AirPlot, LightningPlot })
            {
                plot.Plot.Clear();
                ResetHoverSeries(plot);
                plot.Refresh();
            }
        });
    }
}
