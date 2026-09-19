using System;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using Velopack;
using Velopack.Sources;

namespace CanSensorHub.App;

/// <summary>
/// Jawny punkt wejścia procesu. WPF normalnie generuje własną metodę <c>Main</c> z
/// <c>App.xaml</c>, ale Velopack wymaga, żeby <see cref="VelopackApp"/> wystartował
/// jako pierwsza instrukcja procesu — przed utworzeniem czegokolwiek z WPF. Dlatego
/// <c>App.xaml</c> jest w projekcie zwykłą stroną (Page), a nie ApplicationDefinition.
/// </summary>
public static class Program
{
    /// <summary>Repozytorium, z którego pobierane są wydania. Musi zgadzać się z tym, do którego publikuje workflow release.yml.</summary>
    private const string RepoUrl = "https://github.com/bartkepl/CanSensorHub";

    /// <summary>Po tylu nieudanych próbach dana wersja przestaje być proponowana — żeby zepsute wydanie nie blokowało startu w kółko.</summary>
    private const int MaxUpdateAttempts = 3;

    /// <summary>Limit na sprawdzenie aktualizacji. Po jego przekroczeniu aplikacja startuje normalnie.</summary>
    private static readonly TimeSpan CheckTimeout = TimeSpan.FromSeconds(10);

    // Ten sam katalog, w którym siedzą settings.json i crash.log (patrz AppSettings, App.xaml.cs).
    private static readonly string StateDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CanSensorHub");

    private static readonly string UpdateStateFile = Path.Combine(StateDir, "update_state.json");

    [STAThread]
    public static void Main(string[] args)
    {
        // Obsługuje hooki instalatora (--veloapp-install, --veloapp-updated itd.) i kończy
        // proces, jeśli to właśnie jest takie wywołanie. Musi być pierwsze.
        VelopackApp.Build().Run();

        CheckAndApplyUpdate();

        var app = new App();
        app.InitializeComponent();
        app.Run();
    }

    /// <summary>
    /// Sprawdza wydania w repozytorium i — za zgodą użytkownika — pobiera aktualizację
    /// i restartuje aplikację. Każdy błąd jest tu połykany: brak sieci ani zepsute
    /// wydanie nie mogą uniemożliwić uruchomienia narzędzia, które obsługuje sprzęt.
    /// </summary>
    private static void CheckAndApplyUpdate()
    {
        // Wersja, której dotyczy próba — trzymana poza try, żeby handler błędu mógł ją
        // zapisać bez ponownego (nieograniczonego czasowo) odpytywania sieci.
        string? targetVersion = null;
        try
        {
            var manager = new UpdateManager(new GithubSource(RepoUrl, null, false));

            // Uruchomienie z katalogu build/ albo spod `dotnet run` nie jest instalacją
            // Velopack — nie ma czego aktualizować.
            if (!manager.IsInstalled)
                return;

            // CheckForUpdatesAsync nie ma przeciążenia z tokenem anulowania (Velopack 1.2.0),
            // więc limit czasu wymuszamy ograniczonym oczekiwaniem. Po jego upływie
            // wywołanie w tle jest porzucane, a aplikacja startuje bez aktualizacji.
            var check = manager.CheckForUpdatesAsync();
            if (!check.Wait(CheckTimeout))
            {
                Log("Sprawdzanie aktualizacji przekroczylo limit czasu - pomijam");
                return;
            }

            var update = check.GetAwaiter().GetResult();
            if (update == null)
                return;

            targetVersion = update.TargetFullRelease.Version.ToString();

            var (skippedVersion, failCount) = LoadState();
            if (targetVersion == skippedVersion && failCount >= MaxUpdateAttempts)
            {
                Log($"Pomijam wersje {targetVersion} - {failCount} wczesniejszych niepowodzen");
                return;
            }

            var answer = MessageBox.Show(
                $"Dostępna jest nowa wersja: {targetVersion}\n\n" +
                "Czy chcesz zaktualizować teraz?\n(Aplikacja uruchomi się ponownie po aktualizacji.)",
                "Aktualizacja CanSensorHub",
                MessageBoxButton.YesNo,
                MessageBoxImage.Information);

            if (answer != MessageBoxResult.Yes)
                return;

            Log($"Pobieranie aktualizacji {targetVersion}...");
            manager.DownloadUpdatesAsync(update).GetAwaiter().GetResult();
            Log($"Instalowanie aktualizacji {targetVersion}...");
            manager.ApplyUpdatesAndRestart(update);
        }
        catch (Exception ex)
        {
            // Task.Wait opakowuje błędy w AggregateException — rozpakowujemy dla czytelnego komunikatu.
            var message = (ex as AggregateException)?.GetBaseException().Message ?? ex.Message;
            Log($"Aktualizacja nie powiodla sie: {message}");

            // Licznik niepowodzeń prowadzimy tylko dla konkretnej wersji (etap pobierania
            // lub instalacji). Błąd przed poznaniem wersji to problem z samym sprawdzeniem
            // — przejściowy, nie powód do trwałego pomijania wydania.
            if (targetVersion != null)
                RecordFailure(targetVersion);
        }
    }

    private static (string? Version, int FailCount) LoadState()
    {
        try
        {
            if (!File.Exists(UpdateStateFile)) return (null, 0);
            var json = File.ReadAllText(UpdateStateFile);
            var version = Regex.Match(json, "\"version\"\\s*:\\s*\"([^\"]+)\"");
            var count = Regex.Match(json, "\"failCount\"\\s*:\\s*(\\d+)");
            return (version.Success ? version.Groups[1].Value : null,
                    count.Success ? int.Parse(count.Groups[1].Value) : 0);
        }
        catch { return (null, 0); }
    }

    private static void RecordFailure(string version)
    {
        try
        {
            var (saved, failCount) = LoadState();
            var newCount = version == saved ? failCount + 1 : 1;

            Directory.CreateDirectory(StateDir);
            File.WriteAllText(UpdateStateFile, $"{{\"version\":\"{version}\",\"failCount\":{newCount}}}");
            Log($"Zapisano niepowodzenie {newCount}/{MaxUpdateAttempts} dla wersji {version}");
        }
        catch { }
    }

    private static void Log(string message)
    {
        try
        {
            Directory.CreateDirectory(StateDir);
            File.AppendAllText(Path.Combine(StateDir, "update.log"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}");
        }
        catch { }
    }
}
