using System.Diagnostics;

namespace CanSensorHub.Metrology.Reporting;

/// <summary>
/// Konwersja raportu HTML do PDF przeglądarką z rodziny Chromium uruchomioną bez okna
/// (<c>--headless --print-to-pdf</c>) — docs/adr/0006. Kolejność wyszukiwania: Google Chrome,
/// potem Microsoft Edge (obecny w każdym Windows 10/11). Jeden układ raportu obsługuje oba formaty.
/// </summary>
public static class ChromiumPdfPrinter
{
    /// <summary>Ścieżka pierwszej znalezionej przeglądarki albo <c>null</c>.</summary>
    public static string? FindBrowser(Func<string, bool>? exists = null)
    {
        exists ??= File.Exists;
        foreach (var path in Candidates())
            if (exists(path)) return path;
        return null;
    }

    internal static IEnumerable<string> Candidates()
    {
        string?[] roots =
        [
            Environment.GetEnvironmentVariable("ProgramFiles"),
            Environment.GetEnvironmentVariable("ProgramFiles(x86)"),
            Environment.GetEnvironmentVariable("LOCALAPPDATA"),
        ];
        foreach (var root in roots.Where(r => !string.IsNullOrEmpty(r)))
            yield return Path.Combine(root!, "Google", "Chrome", "Application", "chrome.exe");
        foreach (var root in roots.Where(r => !string.IsNullOrEmpty(r)))
            yield return Path.Combine(root!, "Microsoft", "Edge", "Application", "msedge.exe");
    }

    /// <summary>
    /// Argumenty wywołania. Osobny, tymczasowy katalog profilu jest konieczny: bez niego wywołanie
    /// przy otwartej przeglądarce użytkownika zostałoby przekazane do działającej instancji
    /// i zakończyło się bez utworzenia pliku.
    /// </summary>
    internal static IReadOnlyList<string> Arguments(string htmlPath, string pdfPath, string profileDir) =>
    [
        "--headless=new",
        "--disable-gpu",
        "--no-first-run",
        "--no-default-browser-check",
        "--disable-extensions",
        "--no-pdf-header-footer",
        $"--user-data-dir={profileDir}",
        $"--print-to-pdf={pdfPath}",
        new Uri(Path.GetFullPath(htmlPath)).AbsoluteUri,
    ];

    public static async Task PrintAsync(string htmlPath, string pdfPath, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        var browser = FindBrowser()
            ?? throw new InvalidOperationException("Nie znaleziono przeglądarki Google Chrome ani Microsoft Edge — PDF nie może zostać utworzony. Raport HTML jest dostępny.");

        var profile = Directory.CreateTempSubdirectory("csh-pdf-").FullName;
        try
        {
            if (File.Exists(pdfPath)) File.Delete(pdfPath);
            var psi = new ProcessStartInfo(browser) { UseShellExecute = false, CreateNoWindow = true };
            foreach (var a in Arguments(htmlPath, Path.GetFullPath(pdfPath), profile)) psi.ArgumentList.Add(a);

            using var p = Process.Start(psi) ?? throw new InvalidOperationException($"Nie udało się uruchomić {browser}.");
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout ?? TimeSpan.FromSeconds(60));
            try
            {
                await p.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try { p.Kill(entireProcessTree: true); } catch { /* proces mógł się już zakończyć */ }
                throw new TimeoutException($"{Path.GetFileName(browser)} nie utworzył PDF w wyznaczonym czasie.");
            }

            if (!File.Exists(pdfPath) || new FileInfo(pdfPath).Length == 0)
                throw new InvalidOperationException($"{Path.GetFileName(browser)} zakończył pracę (kod {p.ExitCode}) bez utworzenia pliku PDF.");
        }
        finally
        {
            // Przeglądarka zwalnia pliki profilu chwilę po zakończeniu procesu; nieusunięty katalog
            // tymczasowy nie jest błędem raportu.
            for (var i = 0; i < 5; i++)
            {
                try { Directory.Delete(profile, true); break; }
                catch { await Task.Delay(200, CancellationToken.None).ConfigureAwait(false); }
            }
        }
    }
}
