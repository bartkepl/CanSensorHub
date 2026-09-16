using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using CanSensorHub.App.Services;
using CanSensorHub.App.ViewModels;

namespace CanSensorHub.App;

public partial class App : Application
{
    private static readonly string CrashLogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CanSensorHub", "crash.log");

    private MainViewModel? _mainViewModel;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, ex) => LogCrash("AppDomain", ex.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, ex) =>
        {
            LogCrash("UnobservedTask", ex.Exception);
            ex.SetObserved(); // don't let a background task's exception take the whole process down
        };

        ThemeService.Initialize();

        _mainViewModel = new MainViewModel();
        var window = new MainWindow { DataContext = _mainViewModel };
        MainWindow = window;
        window.Show();
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        LogCrash("Dispatcher", e.Exception);
        MessageBox.Show(e.Exception.Message, "Nieoczekiwany błąd", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }

    private static void LogCrash(string source, Exception? ex)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(CrashLogPath)!);
            File.AppendAllText(CrashLogPath, $"{DateTime.Now:O} [{source}] {ex}\n\n");
        }
        catch
        {
            // best-effort — a failed crash log must never itself crash the process
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _mainViewModel?.Dispose();
        base.OnExit(e);
    }
}
