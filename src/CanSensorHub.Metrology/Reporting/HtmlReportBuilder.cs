using System.Globalization;
using System.Net;
using System.Text;

namespace CanSensorHub.Metrology.Reporting;

/// <summary>Komórka tabeli z opcjonalną klasą CSS (np. <c>pass</c>, <c>fail</c>, <c>warn</c>, <c>num</c>).</summary>
public readonly record struct HtmlCell(string Text, string? Css = null)
{
    public static implicit operator HtmlCell(string text) => new(text);
}

/// <summary>
/// Samodzielny dokument HTML raportu: styl wbudowany, bez skryptów i plików zewnętrznych, więc
/// raport otwiera się w każdej przeglądarce, nadaje się do archiwum i drukuje się na A4 tak samo
/// jak wygląda na ekranie. Cała treść wstawiana przez metody jest escapowana; nieescapowany jest
/// wyłącznie fragment przekazany jawnie do <see cref="Raw"/> (np. wykres SVG).
/// </summary>
public sealed class HtmlReportBuilder
{
    private readonly StringBuilder _body = new();
    private readonly string _title;

    public HtmlReportBuilder(string title) => _title = title;

    public static string Encode(string? text) => WebUtility.HtmlEncode(text ?? "");

    /// <summary>Liczba w zapisie polskim (przecinek dziesiętny), stała liczba miejsc po przecinku.</summary>
    public static string Num(double? value, int decimals, bool sign = false)
    {
        if (value is not { } v || double.IsNaN(v)) return "—";
        var f = "0." + new string('0', decimals);
        var fmt = sign ? $"+{f};-{f};{f}" : f;
        return v.ToString(fmt, CultureInfo.GetCultureInfo("pl-PL"));
    }

    public HtmlReportBuilder Heading(string text, int level = 2)
    {
        _body.Append($"<h{level}>{Encode(text)}</h{level}>\n");
        return this;
    }

    /// <summary>Nowa sekcja; przy druku nie jest rozcinana między strony, jeśli się mieści.</summary>
    public HtmlReportBuilder Section(string title)
    {
        _body.Append($"<section><h2>{Encode(title)}</h2>\n");
        _openSections++;
        return this;
    }

    private int _openSections;

    public HtmlReportBuilder EndSection()
    {
        if (_openSections == 0) return this;
        _body.Append("</section>\n");
        _openSections--;
        return this;
    }

    public HtmlReportBuilder Paragraph(string text, string? css = null)
    {
        _body.Append(css is null ? "<p>" : $"<p class=\"{Encode(css)}\">").Append(Encode(text)).Append("</p>\n");
        return this;
    }

    public HtmlReportBuilder KeyValues(IEnumerable<(string Key, string? Value)> rows)
    {
        _body.Append("<table class=\"kv\">\n");
        foreach (var (k, v) in rows)
            _body.Append($"<tr><th>{Encode(k)}</th><td>{Encode(v ?? "—")}</td></tr>\n");
        _body.Append("</table>\n");
        return this;
    }

    public HtmlReportBuilder Table(IReadOnlyList<string> headers, IEnumerable<IReadOnlyList<HtmlCell>> rows, string? css = null)
    {
        _body.Append(css is null ? "<table>\n" : $"<table class=\"{Encode(css)}\">\n");
        _body.Append("<thead><tr>");
        foreach (var h in headers) _body.Append($"<th>{Encode(h)}</th>");
        _body.Append("</tr></thead>\n<tbody>\n");
        foreach (var row in rows)
        {
            _body.Append("<tr>");
            foreach (var c in row)
                _body.Append(c.Css is null ? "<td>" : $"<td class=\"{Encode(c.Css)}\">").Append(Encode(c.Text)).Append("</td>");
            _body.Append("</tr>\n");
        }
        _body.Append("</tbody></table>\n");
        return this;
    }

    /// <summary>Wstawia fragment bez escapowania. Tylko dla treści generowanej przez kod (SVG), nigdy dla tekstu z zewnątrz.</summary>
    public HtmlReportBuilder Raw(string trustedMarkup)
    {
        _body.Append(trustedMarkup).Append('\n');
        return this;
    }

    public string Build()
    {
        while (_openSections > 0) EndSection();
        return $$"""
            <!DOCTYPE html>
            <html lang="pl">
            <head>
            <meta charset="utf-8">
            <meta name="viewport" content="width=device-width, initial-scale=1">
            <title>{{Encode(_title)}}</title>
            <style>
            {{Css}}
            </style>
            </head>
            <body>
            <h1>{{Encode(_title)}}</h1>
            {{_body}}
            </body>
            </html>
            """;
    }

    private const string Css = """
        @page { size: A4; margin: 14mm 12mm; }
        :root { --fg:#1a1a1a; --muted:#666; --line:#d8d8d8; --head:#f2f4f7; --pass:#1f8a4c; --fail:#c53030; --warn:#b86a00; }
        * { box-sizing: border-box; }
        body { font-family: "Segoe UI", Arial, sans-serif; font-size: 10pt; color: var(--fg); background:#fff; margin: 16px; line-height: 1.35; }
        h1 { font-size: 16pt; margin: 0 0 8px; }
        h2 { font-size: 12pt; margin: 18px 0 6px; padding-bottom: 3px; border-bottom: 1px solid var(--line); }
        section { break-inside: avoid-page; }
        p { margin: 4px 0; }
        p.muted { color: var(--muted); font-size: 9pt; }
        table { border-collapse: collapse; width: 100%; margin: 4px 0 8px; font-variant-numeric: tabular-nums; }
        th, td { border: 1px solid var(--line); padding: 3px 6px; text-align: left; vertical-align: top; }
        thead th { background: var(--head); font-weight: 600; }
        table.kv { width: auto; min-width: 60%; }
        table.kv th { background: var(--head); font-weight: 600; white-space: nowrap; width: 1%; }
        table.compact td, table.compact th { padding: 2px 4px; font-size: 8.5pt; }
        td.num { text-align: right; white-space: nowrap; }
        td.pass { color: var(--pass); font-weight: 700; }
        td.fail { color: var(--fail); font-weight: 700; }
        td.warn { color: var(--warn); }
        .verdict { display:inline-block; padding: 3px 10px; border-radius: 4px; font-weight: 700; color:#fff; }
        .verdict.pass { background: var(--pass); }
        .verdict.fail { background: var(--fail); }
        .verdict.none { background: var(--muted); }
        svg { width: 100%; height: auto; }
        @media print { body { margin: 0; } a { color: inherit; text-decoration: none; } }
        """;
}
