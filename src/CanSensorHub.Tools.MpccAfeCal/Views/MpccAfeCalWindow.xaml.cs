using System.Collections.Specialized;
using System.Windows;
using CanSensorHub.Tools.MpccAfeCal.ViewModels;

namespace CanSensorHub.Tools.MpccAfeCal.Views;

public partial class MpccAfeCalWindow : Window
{
    // Tableau 10 — te same kolory co na wykresach modułów, w kolejności kanałów.
    private static readonly string[] ChannelColors =
        ["#4E79A7", "#F28E2B", "#E15759", "#76B7B2", "#59A14F", "#EDC948", "#B07AA1"];

    private readonly MpccAfeCalViewModel _vm;

    public MpccAfeCalWindow(MpccAfeCalViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
        vm.ErrorSeriesChanged += (_, series) => RedrawErrorPlot(series);
        vm.Log.CollectionChanged += OnLogChanged;
        // Wpisy dodane, gdy zakładka dziennika była ukryta, nie przewinęły listy — przewinięcie po jej pokazaniu.
        LogList.IsVisibleChanged += (_, e) => { if (e.NewValue is true) ScrollLogToEnd(); };
        ApplyPlotTheme();
        // Panel legendy dodaje się raz: ShowLegend(Edge) dokłada nowy panel przy każdym wywołaniu.
        ErrorPlot.Plot.ShowLegend(ScottPlot.Edge.Right);
        RedrawErrorPlot([]);
        Closed += (_, _) => _vm.Dispose();
    }

    private void OnLogChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Add) ScrollLogToEnd();
    }

    private void ScrollLogToEnd() => Dispatcher.InvokeAsync(() =>
    {
        if (LogList.Items.Count > 0) LogList.ScrollIntoView(LogList.Items[^1]);
    });

    /// <summary>
    /// Błąd węzła względem wzorca w funkcji napięcia: przed kalibracją linia przerywana z punktami,
    /// w sprawdzeniu linia ciągła. Linie kropkowane wyznaczają pasmo tolerancji.
    /// </summary>
    private void RedrawErrorPlot(IReadOnlyList<ErrorSeries> seriesList)
    {
        var plot = ErrorPlot.Plot;
        plot.Clear();
        plot.Axes.Bottom.Label.Text = "Napięcie wzorca [V]";
        plot.Axes.Left.Label.Text = "Błąd węzła [mV]";

        foreach (var s in seriesList.OrderBy(s => s.AfterCalibration).ThenBy(s => s.Channel))
        {
            var pts = s.Points.OrderBy(p => p.Reference).ToArray();
            var scatter = plot.Add.Scatter(pts.Select(p => p.Reference).ToArray(), pts.Select(p => p.ErrorMv).ToArray());
            scatter.Color = ScottPlot.Color.FromHex(ChannelColors[s.Channel % ChannelColors.Length]);
            scatter.LegendText = $"CH{s.Channel} {(s.AfterCalibration ? "sprawdzenie" : "przed")}";
            if (s.AfterCalibration)
            {
                scatter.LineWidth = 2.5f;
                scatter.MarkerSize = 6;
            }
            else
            {
                scatter.LineWidth = 1.2f;
                scatter.LinePattern = ScottPlot.LinePattern.Dashed;
                scatter.MarkerSize = 4;
            }
        }

        var tol = _vm.Settings.ToleranceMv;
        foreach (var y in new[] { tol, -tol })
        {
            var line = plot.Add.HorizontalLine(y);
            line.LinePattern = ScottPlot.LinePattern.Dotted;
            line.Color = ScottPlot.Colors.Gray;
        }
        plot.Add.HorizontalLine(0).Color = ScottPlot.Colors.Gray.WithAlpha(0.5);

        plot.Axes.AutoScale();
        ErrorPlot.Refresh();
    }

    private void ApplyPlotTheme()
    {
        if (Application.Current?.TryFindResource("ThemeMarker") as string != "Dark") return;
        var plot = ErrorPlot.Plot;
        plot.FigureBackground.Color = ScottPlot.Color.FromHex("#1E1F22");
        plot.DataBackground.Color = ScottPlot.Color.FromHex("#2B2D30");
        plot.Axes.Color(ScottPlot.Color.FromHex("#E6E6E6"));
        plot.Grid.MajorLineColor = ScottPlot.Color.FromHex("#3F4144");
        plot.Legend.BackgroundColor = ScottPlot.Color.FromHex("#2B2D30");
        plot.Legend.FontColor = ScottPlot.Color.FromHex("#E6E6E6");
        plot.Legend.OutlineColor = ScottPlot.Color.FromHex("#3F4144");
    }
}
