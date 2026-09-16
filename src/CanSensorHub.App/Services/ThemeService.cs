using System.Windows;
using Microsoft.Win32;

namespace CanSensorHub.App.Services;

/// <summary>
/// Follows the Windows "Apps use light/dark mode" setting (as requested — no manual toggle): reads
/// HKCU\...\Personalize\AppsUseLightTheme at startup and swaps the merged theme dictionary live whenever
/// the user changes it in Windows Settings.
/// </summary>
public static class ThemeService
{
    public static void Initialize()
    {
        Apply(IsWindowsLightTheme());
        Microsoft.Win32.SystemEvents.UserPreferenceChanged += (_, e) =>
        {
            if (e.Category != UserPreferenceCategory.General) return;
            Application.Current?.Dispatcher.Invoke(() => Apply(IsWindowsLightTheme()));
        };
    }

    private static bool IsWindowsLightTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int v ? v != 0 : true;
        }
        catch
        {
            return true;
        }
    }

    private static void Apply(bool light)
    {
        var app = Application.Current;
        if (app is null) return;

        var uri = new Uri(light ? "Themes/Light.xaml" : "Themes/Dark.xaml", UriKind.Relative);
        var newDict = new ResourceDictionary { Source = uri };

        var old = app.Resources.MergedDictionaries.FirstOrDefault(d => d.Contains("ThemeMarker"));
        if (old is not null) app.Resources.MergedDictionaries.Remove(old);
        app.Resources.MergedDictionaries.Add(newDict);
    }
}
