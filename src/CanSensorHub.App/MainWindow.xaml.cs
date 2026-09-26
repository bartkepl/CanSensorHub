using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using CanSensorHub.Core.Tools;
using CanSensorHub.App.ViewModels;
using CanSensorHub.App.Views;

namespace CanSensorHub.App;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private MainViewModel? _vm;
    private BootloaderToolWindow? _bootloaderWindow;

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_vm is not null) _vm.AddDeviceRequested -= OnAddDeviceRequested;
        _vm = e.NewValue as MainViewModel;
        if (_vm is not null) _vm.AddDeviceRequested += OnAddDeviceRequested;
    }

    private void OnOpenBootloaderTool(object sender, RoutedEventArgs e)
    {
        if (_vm is null) return;
        if (_bootloaderWindow is not null)
        {
            _bootloaderWindow.Activate();
            return;
        }

        var vm = new BootloaderToolViewModel(_vm.Bus, _vm.Registry);
        _bootloaderWindow = new BootloaderToolWindow(vm) { Owner = this };
        _bootloaderWindow.Closed += (_, _) => _bootloaderWindow = null;
        // Independent, non-blocking tool window: unlike AddDeviceDialog this one is meant to stay open
        // and usable alongside the rest of the app while a flash is in progress, so the owner is not disabled.
        _bootloaderWindow.Show();
    }

    private void OnOpenToolsMenu(object sender, RoutedEventArgs e)
    {
        if (ToolsButton.ContextMenu is not { } menu) return;
        menu.DataContext = _vm;
        menu.PlacementTarget = ToolsButton;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private void OnToolMenuItemClick(object sender, RoutedEventArgs e)
    {
        if (_vm is null || (sender as MenuItem)?.DataContext is not IHubTool tool) return;
        try
        {
            tool.Open(_vm.CreateToolContext(this));
        }
        catch (Exception ex)
        {
            _vm.LastError = $"Nie udało się otworzyć narzędzia „{tool.DisplayName}”: {ex.Message}";
        }
    }

    private void OnAddDeviceRequested(object? sender, EventArgs e)
    {
        if (_vm is null) return;
        var dialogVm = new AddDeviceDialogViewModel(_vm.Registry);
        var dialog = new AddDeviceDialog(dialogVm) { Owner = this };

        // Shown non-modally (Show, not ShowDialog) with the owner manually disabled: functionally the same
        // "can't touch the main window until this closes" behavior as a modal dialog, but avoids relying on
        // Window.ShowDialog()'s nested Dispatcher.PushFrame() message loop, which this environment's window
        // manager was observed to never complete.
        IsEnabled = false;
        dialog.Closed += (_, _) =>
        {
            IsEnabled = true;
            Activate();
            if (dialog.Accepted && dialog.ResultDescriptor is not null)
                _vm.AddDeviceInstance(dialog.ResultDescriptor, dialog.ResultNodeId, dialog.ResultInstanceName);
        };
        dialog.Show();
    }
}
