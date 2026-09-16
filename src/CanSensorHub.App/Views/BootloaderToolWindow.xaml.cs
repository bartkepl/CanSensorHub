using System.Collections.Specialized;
using System.Windows;
using CanSensorHub.App.ViewModels;

namespace CanSensorHub.App.Views;

public partial class BootloaderToolWindow : Window
{
    public BootloaderToolWindow(BootloaderToolViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
        vm.LogLines.CollectionChanged += OnLogLinesChanged;
    }

    private void OnLogLinesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (LogListBox.Items.Count == 0) return;
        Dispatcher.InvokeAsync(() => LogListBox.ScrollIntoView(LogListBox.Items[^1]));
    }
}
