using System.Globalization;
using System.Net;
using System.Text;

namespace CanSensorHub.Metrology.Reporting;

/// <summary>Seria wykresu: linia ciągła albo przerywana, z punktami pomiarowymi.</summary>
public sealed record SvgSeries(string Label, string ColorHex, IReadOnlyList<(double X, double Y)> Points, bool Dashed = false, double StrokeWidth = 1.5);

/// <summary>
/// Wykres liniowy jako SVG, bez bibliotek i skryptów — do osadzenia w raporcie HTML i wydruku.
/// Oś Y obejmuje dane i linie odniesienia; podziałka dobierana do „okrągłych” wartości 1–2–5.
/// </summary>
public sealed class SvgLineChart
{
    private const double W = 900, H = 420;
    private const double Left = 64, Right = 170, Top = 16, Bottom = 50;

    public string XLabel { get; init; } = "";
    public string YLabel { get; init; } = "";
    public List<SvgSeries> Series { get; } = [];
    /// <summary>Poziome linie odniesienia (np. granice tolerancji), rysowane kropkowane.</summary>
    public List<double> ReferenceLines { get; } = [];

    public string Render()
    {
        var pts = Series.SelectMany(s => s.Points).ToList();
        var (x0, x1) = pts.Count > 0 ? (pts.Min(p => p.X), pts.Max(p => p.X)) : (0.0, 1.0);
        var ys = pts.Select(p => p.Y).Concat(ReferenceLines).Append(0.0).ToList();
        var (y0, y1) = (ys.Min(), ys.Max());
        var xt = NiceTicks(x0, x1, 10);
        var yt = NiceTicks(y0, y1, 8);
        double xmin = xt[0], xmax = xt[^1], ymin = yt[0], ymax = yt[^1];
        double pw = W - Left - Right, ph = H - Top - Bottom;
        double X(double v) => Left + (v - xmin) / (xmax - xmin) * pw;
        double Y(double v) => Top + (ymax - v) / (ymax - ymin) * ph;

        var sb = new StringBuilder();
        sb.Append($"<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 {F(W)} {F(H)}\" font-family=\"Segoe UI, Arial, sans-serif\" font-size=\"11\">");
        sb.Append($"<rect x=\"{F(Left)}\" y=\"{F(Top)}\" width=\"{F(pw)}\" height=\"{F(ph)}\" fill=\"#fff\" stroke=\"#999\"/>");

        foreach (var t in xt)
        {
            sb.Append($"<line x1=\"{F(X(t))}\" y1=\"{F(Top)}\" x2=\"{F(X(t))}\" y2=\"{F(Top + ph)}\" stroke=\"#e6e6e6\"/>");
            sb.Append($"<text x=\"{F(X(t))}\" y=\"{F(Top + ph + 15)}\" text-anchor=\"middle\">{Label(t)}</text>");
        }
        foreach (var t in yt)
        {
            sb.Append($"<line x1=\"{F(Left)}\" y1=\"{F(Y(t))}\" x2=\"{F(Left + pw)}\" y2=\"{F(Y(t))}\" stroke=\"#e6e6e6\"/>");
            sb.Append($"<text x=\"{F(Left - 6)}\" y=\"{F(Y(t) + 4)}\" text-anchor=\"end\">{Label(t)}</text>");
        }
        sb.Append($"<line x1=\"{F(Left)}\" y1=\"{F(Y(0))}\" x2=\"{F(Left + pw)}\" y2=\"{F(Y(0))}\" stroke=\"#888\"/>");
        foreach (var r in ReferenceLines)
            sb.Append($"<line x1=\"{F(Left)}\" y1=\"{F(Y(r))}\" x2=\"{F(Left + pw)}\" y2=\"{F(Y(r))}\" stroke=\"#555\" stroke-dasharray=\"2,3\"/>");

        foreach (var s in Series.Where(s => s.Points.Count > 0))
        {
            var ordered = s.Points.OrderBy(p => p.X).ToList();
            var dash = s.Dashed ? " stroke-dasharray=\"6,4\"" : "";
            sb.Append($"<polyline fill=\"none\" stroke=\"{Enc(s.ColorHex)}\" stroke-width=\"{F(s.StrokeWidth)}\"{dash} points=\"");
            sb.Append(string.Join(' ', ordered.Select(p => $"{F(X(p.X))},{F(Y(p.Y))}")));
            sb.Append("\"/>");
            foreach (var p in ordered)
                sb.Append($"<circle cx=\"{F(X(p.X))}\" cy=\"{F(Y(p.Y))}\" r=\"{(s.Dashed ? "2" : "2.8")}\" fill=\"{Enc(s.ColorHex)}\"/>");
        }

        sb.Append($"<text x=\"{F(Left + pw / 2)}\" y=\"{F(H - 8)}\" text-anchor=\"middle\" font-weight=\"600\">{Enc(XLabel)}</text>");
        sb.Append($"<text transform=\"translate(14 {F(Top + ph / 2)}) rotate(-90)\" text-anchor=\"middle\" font-weight=\"600\">{Enc(YLabel)}</text>");

        var ly = Top + 6.0;
        foreach (var s in Series.Where(s => s.Points.Count > 0))
        {
            var lx = Left + pw + 14;
            var dash = s.Dashed ? " stroke-dasharray=\"6,4\"" : "";
            sb.Append($"<line x1=\"{F(lx)}\" y1=\"{F(ly)}\" x2=\"{F(lx + 24)}\" y2=\"{F(ly)}\" stroke=\"{Enc(s.ColorHex)}\" stroke-width=\"{F(s.StrokeWidth)}\"{dash}/>");
            sb.Append($"<text x=\"{F(lx + 30)}\" y=\"{F(ly + 4)}\">{Enc(s.Label)}</text>");
            ly += 16;
        }
        sb.Append("</svg>");
        return sb.ToString();
    }

    /// <summary>Podziałka „okrągła” (krok 1, 2 albo 5 × 10ⁿ), obejmująca [min, max].</summary>
    public static IReadOnlyList<double> NiceTicks(double min, double max, int target)
    {
        if (max <= min) { min -= 1; max += 1; }
        var raw = (max - min) / Math.Max(1, target);
        var mag = Math.Pow(10, Math.Floor(Math.Log10(raw)));
        var step = new[] { 1.0, 2.0, 5.0, 10.0 }.Select(m => m * mag).First(s => s >= raw);
        var start = Math.Floor(min / step) * step;
        var end = Math.Ceiling(max / step) * step;
        var ticks = new List<double>();
        for (var v = start; v <= end + step * 1e-9; v += step) ticks.Add(Math.Round(v / step) * step);
        return ticks;
    }

    private static string F(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);
    private static string Label(double v) => (Math.Abs(v) < 1e-12 ? 0 : v).ToString("0.###", CultureInfo.GetCultureInfo("pl-PL"));
    private static string Enc(string s) => WebUtility.HtmlEncode(s);
}
