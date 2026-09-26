using CanSensorHub.Metrology.Reporting;

namespace CanSensorHub.Metrology.Tests;

public class HtmlReportBuilderTests
{
    [Fact]
    public void Escapes_all_text_content()
    {
        var html = new HtmlReportBuilder("Raport <test>")
            .Section("A & B")
            .Paragraph("<script>alert(1)</script>")
            .KeyValues([("klucz", "\"wartość\"")])
            .Table(["h<1>"], [[new HtmlCell("x&y", "pass")]])
            .Build();

        Assert.DoesNotContain("<script>", html);
        Assert.Contains("&lt;script&gt;", html);
        Assert.Contains("Raport &lt;test&gt;", html);
        Assert.Contains("A &amp; B", html);
        Assert.Contains("<td class=\"pass\">x&amp;y</td>", html);
        Assert.Contains("</section>", html);   // niezamknięta sekcja zamykana przy Build
    }

    [Theory]
    [InlineData(1.5, 2, false, "1,50")]
    [InlineData(0.8, 2, true, "+0,80")]
    [InlineData(-0.8, 1, true, "-0,8")]
    [InlineData(double.NaN, 2, false, "—")]
    public void Formats_numbers_with_polish_decimal_separator(double v, int d, bool sign, string expected) =>
        Assert.Equal(expected, HtmlReportBuilder.Num(v, d, sign));
}

public class SvgLineChartTests
{
    [Fact]
    public void Renders_series_reference_lines_and_legend()
    {
        var chart = new SvgLineChart { XLabel = "X", YLabel = "Y" };
        chart.Series.Add(new SvgSeries("CH0 <a>", "#4E79A7", [(0.1, -1.0), (4.8, 2.0)], Dashed: true));
        chart.ReferenceLines.AddRange([5, -5]);
        var svg = chart.Render();

        Assert.StartsWith("<svg", svg);
        Assert.Contains("<polyline", svg);
        Assert.Contains("stroke-dasharray=\"6,4\"", svg);
        Assert.Contains("stroke-dasharray=\"2,3\"", svg);
        Assert.Contains("CH0 &lt;a&gt;", svg);
    }

    [Fact]
    public void Nice_ticks_cover_range_with_round_steps()
    {
        var t = SvgLineChart.NiceTicks(0.1, 4.8, 10);
        Assert.True(t[0] <= 0.1 && t[^1] >= 4.8);
        Assert.Equal(0.5, t[1] - t[0], 9);

        var y = SvgLineChart.NiceTicks(-22.3, 5, 8);
        Assert.True(y[0] <= -22.3 && y[^1] >= 5);
        Assert.Equal(5, y[1] - y[0], 9);
    }
}

public class ChromiumPdfPrinterTests
{
    [Fact]
    public void Prefers_chrome_over_edge()
    {
        var found = ChromiumPdfPrinter.FindBrowser(p => p.EndsWith("chrome.exe") || p.EndsWith("msedge.exe"));
        Assert.EndsWith("chrome.exe", found);
    }

    [Fact]
    public void Falls_back_to_edge_and_reports_absence()
    {
        Assert.EndsWith("msedge.exe", ChromiumPdfPrinter.FindBrowser(p => p.EndsWith("msedge.exe")));
        Assert.Null(ChromiumPdfPrinter.FindBrowser(_ => false));
    }

    [Fact]
    public void Uses_separate_profile_and_file_uri()
    {
        var args = ChromiumPdfPrinter.Arguments(@"C:\r\raport.html", @"C:\r\raport.pdf", @"C:\tmp\profil");
        Assert.Contains(@"--user-data-dir=C:\tmp\profil", args);
        Assert.Contains(@"--print-to-pdf=C:\r\raport.pdf", args);
        Assert.Contains(args, a => a.StartsWith("--headless"));
        Assert.Equal("file:///C:/r/raport.html", args[^1]);
    }
}
