using System.Windows;
using CanSensorHub.Core.Modules;
using CanSensorHub.App.ViewModels;

namespace CanSensorHub.App.Views;

/// <summary>
/// Shown with <see cref="Window.Show"/> + a manually-disabled owner rather than <see cref="Window.ShowDialog"/>:
/// deliberate choice, not an oversight — see MainWindow.xaml.cs for why.
/// </summary>
public partial class AddDeviceDialog : Window
{
    private readonly AddDeviceDialogViewModel _vm;

    public bool Accepted { get; private set; }
    public IDeviceModuleDescriptor? ResultDescriptor { get; private set; }
    public byte ResultNodeId { get; private set; }
    public string ResultInstanceName { get; private set; } = "";

    public AddDeviceDialog(AddDeviceDialogViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
    }

    private void OnAdd(object sender, RoutedEventArgs e)
    {
        if (!_vm.TryBuild(out var nodeId, out var name)) return;
        ResultDescriptor = _vm.SelectedModule;
        ResultNodeId = nodeId;
        ResultInstanceName = name;
        Accepted = true;
        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e) => Close();
}
