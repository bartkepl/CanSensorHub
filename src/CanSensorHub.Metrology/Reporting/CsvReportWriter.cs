using System.Globalization;
using System.Text;

namespace CanSensorHub.Metrology.Reporting;

/// <summary>
/// Raport kalibracji w jednym pliku CSV złożonym z sekcji: nagłówek sekcji <c>[nazwa]</c>, pary
/// klucz–wartość albo tabela. Separator <c>,</c> i kropka dziesiętna niezależnie od ustawień
/// regionalnych — jak w pozostałych plikach CSV aplikacji. <see cref="Flush"/> utrwala zapis na dysku
/// (także przy zamknięciu), więc długie tabele mogą być utrwalane częściami.
/// </summary>
public sealed class CsvReportWriter : IDisposable
{
    private readonly StreamWriter _writer;
    private bool _hasSections;
    public string FilePath { get; }

    public CsvReportWriter(string filePath)
    {
        FilePath = filePath;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(filePath))!);
        _writer = new StreamWriter(new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.Read), new UTF8Encoding(true));
    }

    public void Section(string title)
    {
        if (_hasSections) _writer.WriteLine();
        _hasSections = true;
        _writer.WriteLine(Escape($"[{title}]"));
    }

    public void KeyValue(string key, object? value) => Row(key, value);

    public void Row(params object?[] cells)
    {
        _writer.WriteLine(string.Join(',', cells.Select(c => Escape(Format(c)))));
    }

    public void Flush()
    {
        _writer.Flush();
        if (_writer.BaseStream is FileStream fs) fs.Flush(true);
    }

    public static string Format(object? value) => value switch
    {
        null => "",
        double d when double.IsNaN(d) => "",
        double d => d.ToString("G9", CultureInfo.InvariantCulture),
        float f => f.ToString("G9", CultureInfo.InvariantCulture),
        DateTimeOffset t => t.ToString("yyyy-MM-ddTHH:mm:sszzz", CultureInfo.InvariantCulture),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? "",
    };

    private static string Escape(string s) =>
        s.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? $"\"{s.Replace("\"", "\"\"")}\"" : s;

    public void Dispose()
    {
        Flush();
        _writer.Dispose();
    }
}
