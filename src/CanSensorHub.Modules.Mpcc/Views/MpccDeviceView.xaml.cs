using System.ComponentModel;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using CanSensorHub.Modules.Mpcc.ViewModels;

namespace CanSensorHub.Modules.Mpcc.Views;

/// <summary>
/// Code-behind only wires the two ScottPlot charts to the view model's chart samples — ScottPlot's
/// imperative plot-object API doesn't lend itself to pure XAML binding, so this is the one place the
/// module intentionally steps outside strict MVVM. Which curves exist, their colors and whether they are
/// shown all come from <see cref="ChartSeriesVm"/>; the legend is plain WPF bound to the same objects
/// (ScottPlot's own legend is a bitmap and can't be clicked), so this class only buffers and draws.
/// </summary>
public partial class MpccDeviceView : UserControl
{
    // Time-based, not count-based: channels/sensors update at very different effective rates (fused
    // telemetry pushed on TELEMETRY_PERIOD vs. per-sensor readings only arriving on demand or on
    // auto-poll), so a fixed sample COUNT gives each series a different rolling time span — the fast ones
    // look dense-and-short, the slow ones stay wide. Trimming by elapsed seconds instead keeps every
    // curve's visible window the same width once trimming kicks in.
    private const double MaxWindowSeconds = 600;

    // Keyed by ChartSeriesVm.Key — buffered regardless of visibility so switching a curve on later still
    // has history to show.
    private readonly Dictionary<string, (List<double> Xs, List<double> Ys)> _buffers = [];

    private readonly DateTime _start = DateTime.UtcNow;

    // Hover-to-inspect: which plotted curves live on each plot right now (rebuilt every redraw, since
    // Plot.Clear() drops the ScottPlot.Plottables.Scatter instances) and one WPF ToolTip per plot that
    // follows the mouse and shows the value of whichever curve's point is nearest the cursor.
    private readonly Dictionary<ScottPlot.WPF.WpfPlot, List<(ScottPlot.Plottables.Scatter Scatter, string Label)>> _hoverSeries = [];
    private readonly Dictionary<ScottPlot.WPF.WpfPlot, ToolTip> _hoverTooltips = [];

    public MpccDeviceView()
    {
        InitializeComponent();

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
                foreach (var s in oldVm.TempSeries.Concat(oldVm.VoltageSeries)) s.PropertyChanged -= OnSeriesChanged;
            }
            if (e.NewValue is MpccDeviceViewModel vm)
            {
                vm.ChartSampleReceived += OnSample;
                vm.ChartsClearRequested += OnClearRequested;
                foreach (var s in vm.TempSeries.Concat(vm.VoltageSeries)) s.PropertyChanged += OnSeriesChanged;
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
            if (DataContext is not MpccDeviceViewModel vm || vm.FindChartSeries(e.SeriesKey) is not { } series) return;
            var t = (DateTime.UtcNow - _start).TotalSeconds;
            if (!_buffers.TryGetValue(e.SeriesKey, out var buf)) { buf = ([], []); _buffers[e.SeriesKey] = buf; }
            buf.Xs.Add(t);
            buf.Ys.Add(e.Value);
            TrimOld(buf.Xs, buf.Ys, t);
            if (series.IsChecked) Redraw(series.Target);
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

    private void Redraw(ChartTarget target)
    {
        if (DataContext is not MpccDeviceViewModel vm) return;
        if (target == ChartTarget.Temp) RedrawPlot(TempPlot, vm.TempSeries);
        else if (target == ChartTarget.Voltage) RedrawPlot(VoltagePlot, vm.VoltageSeries);
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
            // An averaged channel and its sensor reading share a color (same measurement), so the line
            // style is what tells them apart: solid for the average, dashed with sample markers for the
            // on-demand sensor reading. The legend swatch in the XAML mirrors this.
            if (series.IsAverage)
            {
                scatter.LineWidth = 2.5f;
                scatter.MarkerSize = 0;
            }
            else
            {
                scatter.LineWidth = 1.5f;
                scatter.LinePattern = ScottPlot.LinePattern.Dashed;
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
        Dispatcher.InvokeAsync(() =>
        {
            _buffers.Clear();
            foreach (var plot in new[] { TempPlot, VoltagePlot })
            {
                plot.Plot.Clear();
                ResetHoverSeries(plot);
                plot.Refresh();
            }
        });
    }
}
