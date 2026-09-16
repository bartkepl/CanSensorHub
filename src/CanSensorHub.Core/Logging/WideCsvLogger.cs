using System.Globalization;

namespace CanSensorHub.Core.Logging;

/// <summary>
/// Safe wide-format CSV logger: "czas_iso,unix" + one column per measurement key. Flushes and fsyncs
/// after every row, so a lost connection / crash / Ctrl+C never loses an already-written sample — the
/// same durability trade-off as the reference Python loggers.
/// </summary>
public sealed class WideCsvLogger : IDisposable
{
    private readonly StreamWriter _writer;
    public IReadOnlyList<string> Columns { get; }
    public string FilePath { get; }
    public int RowsWritten { get; private set; }

    public WideCsvLogger(string filePath, IEnumerable<string> columns)
    {
        FilePath = filePath;
        Columns = columns.ToArray();
        var stream = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.Read);
        _writer = new StreamWriter(stream, System.Text.Encoding.UTF8);
        _writer.WriteLine(string.Join(',', new[] { "czas_iso", "unix" }.Concat(Columns)));
        FlushToDisk();
    }

    public void WriteRow(LiveValueStore store, TimeSpan staleness, int decimalPlaces = 3)
    {
        var snapshot = store.Snapshot(staleness);
        var now = DateTimeOffset.Now;
        var cells = new List<string>(Columns.Count + 2)
        {
            now.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture),
            now.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture),
        };
        foreach (var col in Columns)
            cells.Add(snapshot.TryGetValue(col, out var v) ? v.ToString($"F{decimalPlaces}", CultureInfo.InvariantCulture) : "");

        _writer.WriteLine(string.Join(',', cells));
        FlushToDisk();
        RowsWritten++;
    }

    private void FlushToDisk()
    {
        _writer.Flush();
        if (_writer.BaseStream is FileStream fs)
            fs.Flush(true);
    }

    public void Dispose() => _writer.Dispose();
}
