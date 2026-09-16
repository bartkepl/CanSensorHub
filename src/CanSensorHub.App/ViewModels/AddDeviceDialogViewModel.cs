using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CanSensorHub.Core.Modules;

namespace CanSensorHub.App.ViewModels;

/// <summary>Backing view model for the "Add device" dialog: pick a module type (ComboBox, closed catalog) and a CAN node address (free text — any byte 1..254, hex or decimal).</summary>
public partial class AddDeviceDialogViewModel : ObservableObject
{
    public IReadOnlyList<IDeviceModuleDescriptor> Modules { get; }

    [ObservableProperty] private IDeviceModuleDescriptor? _selectedModule;
    [ObservableProperty] private string _nodeIdText = "";
    [ObservableProperty] private string _instanceName = "";
    [ObservableProperty] private string? _validationError;

    public AddDeviceDialogViewModel(ModuleRegistry registry)
    {
        Modules = registry.Modules;
        SelectedModule = Modules.FirstOrDefault();
    }

    partial void OnSelectedModuleChanged(IDeviceModuleDescriptor? value)
    {
        if (value is null) return;
        NodeIdText = $"0x{value.DefaultNodeId:X2}";
        InstanceName = value.ShortName;
    }

    public bool TryBuild(out byte nodeId, out string instanceName)
    {
        nodeId = 0;
        instanceName = string.IsNullOrWhiteSpace(InstanceName) ? SelectedModule?.ShortName ?? "Urządzenie" : InstanceName.Trim();

        if (SelectedModule is null)
        {
            ValidationError = "Wybierz typ modułu.";
            return false;
        }

        var text = NodeIdText.Trim();
        var ok = text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? byte.TryParse(text.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out nodeId)
            : byte.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out nodeId);

        if (!ok || nodeId is 0 or 0xFF)
        {
            ValidationError = "Podaj adres węzła 1..254 (np. 0x10 albo 16). 0 i 0xFF są zarezerwowane.";
            return false;
        }

        ValidationError = null;
        return true;
    }
}
