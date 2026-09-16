using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.IO.Ports;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CanSensorHub.Core.Can;
using CanSensorHub.Core.Modules;
using CanSensorHub.Modules.Mpcc;
using CanSensorHub.Modules.Mpswp;
using CanSensorHub.App.Services;

namespace CanSensorHub.App.ViewModels;

public sealed record TransportKindOption(string Key, string Label);

/// <summary>
/// Shell view model: owns the single shared <see cref="CanBusService"/> connection (the app's core
/// responsibility per the brief — manage the WeAct adapter and the bus traffic log), the module catalog,
/// and the list of device instances the user has added. Individual device UIs live entirely inside each
/// module's own view model; this class only creates/destroys/tracks them.
/// </summary>
public partial class MainViewModel : ObservableObject, IDisposable
{
    public CanBusService Bus { get; } = new();
    public ModuleRegistry Registry { get; } = new();
    public BusMonitorViewModel BusMonitor { get; }
    public ObservableCollection<DeviceEntry> Devices { get; } = [];

    /// <summary>
    /// What the shell's single TabControl actually binds to: <see cref="BusMonitor"/> pinned as item 0
    /// (module-agnostic bus log — always present, never closable), followed by <see cref="Devices"/> in
    /// order. Kept in sync with <see cref="Devices"/> via <see cref="OnDevicesChanged"/> rather than
    /// merged in XAML (a CompositeCollection) so both item types can use plain implicit DataTemplates.
    /// </summary>
    public ObservableCollection<object> TabItems { get; } = [];
    [ObservableProperty] private object? _selectedTabItem;

    public IReadOnlyList<int> AvailableBitrates { get; } = SlcanTransport.SupportedBitrates;
    public IReadOnlyList<TransportKindOption> TransportKinds { get; } =
    [
        new("Simulated", "Symulator (bez sprzętu)"),
        new("Slcan", "Adapter WeAct USB2CANFDV1 (slcan)"),
    ];

    [ObservableProperty] private ObservableCollection<string> _availablePorts = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSlcanMode))]
    private string _selectedTransportKind = "Simulated"; // "Simulated" | "Slcan"

    public bool IsSlcanMode => SelectedTransportKind == "Slcan";
    [ObservableProperty] private string? _selectedPort;
    [ObservableProperty] private int _selectedBitrate = 500000;
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConnectCommand))]
    [NotifyCanExecuteChangedFor(nameof(DisconnectCommand))]
    [NotifyCanExecuteChangedFor(nameof(AddDeviceCommand))]
    private bool _isConnected;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConnectCommand))]
    private bool _isConnecting;
    [ObservableProperty] private string _connectionStatusText = "Rozłączono";
    [ObservableProperty] private string? _lastError;

    private List<DeviceSettingsEntry> _pendingDevices = [];

    /// <summary>Raised when the user clicks "Add device"; MainWindow's code-behind owns showing the dialog (kept out of the view model to avoid a View dependency here).</summary>
    public event EventHandler? AddDeviceRequested;

    [RelayCommand(CanExecute = nameof(IsConnected))]
    private void AddDevice() => AddDeviceRequested?.Invoke(this, EventArgs.Empty);

    public MainViewModel()
    {
        Registry.Register(new MpccModuleDescriptor());
        Registry.Register(new MpswpModuleDescriptor());
        BusMonitor = new BusMonitorViewModel(Bus);
        TabItems.Add(BusMonitor);
        SelectedTabItem = BusMonitor;
        Devices.CollectionChanged += OnDevicesChanged;
        Bus.ConnectionChanged += (_, _) =>
        {
            IsConnected = Bus.IsConnected;
            ConnectionStatusText = Bus.IsConnected ? $"Połączono — {Bus.ConnectionDescription}" : "Rozłączono";
        };

        RefreshPorts();

        var settings = AppSettingsService.Load();
        SelectedTransportKind = settings.TransportKind;
        SelectedPort = settings.Port ?? AvailablePorts.FirstOrDefault();
        SelectedBitrate = AvailableBitrates.Contains(settings.Bitrate) ? settings.Bitrate : 500000;
        _pendingDevices = settings.Devices;
    }

    private void OnDevicesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        switch (e.Action)
        {
            case NotifyCollectionChangedAction.Add:
                foreach (DeviceEntry item in e.NewItems!) TabItems.Add(item);
                break;
            case NotifyCollectionChangedAction.Remove:
                foreach (DeviceEntry item in e.OldItems!) TabItems.Remove(item);
                break;
            case NotifyCollectionChangedAction.Reset:
                TabItems.Clear();
                TabItems.Add(BusMonitor);
                foreach (var d in Devices) TabItems.Add(d);
                break;
        }
    }

    [RelayCommand]
    private void RefreshPorts()
    {
        var current = SelectedPort;
        AvailablePorts = new ObservableCollection<string>(SerialPort.GetPortNames().OrderBy(p => p));
        if (current is not null && AvailablePorts.Contains(current)) SelectedPort = current;
    }

    [RelayCommand(CanExecute = nameof(CanConnect))]
    private async Task Connect()
    {
        LastError = null;
        IsConnecting = true;
        try
        {
            if (SelectedTransportKind == "Slcan")
            {
                if (string.IsNullOrWhiteSpace(SelectedPort))
                {
                    LastError = "Wybierz port COM adaptera WeAct.";
                    return;
                }
                await Bus.ConnectSlcanAsync(SelectedPort, SelectedBitrate);
            }
            else
            {
                await Bus.ConnectSimulatedAsync();
            }

            RestorePendingDevices();
            SaveSettings();
        }
        catch (Exception ex)
        {
            LastError = $"Nie udało się połączyć: {ex.Message}";
        }
        finally
        {
            IsConnecting = false;
        }
    }

    private bool CanConnect() => !IsConnected && !IsConnecting;

    [RelayCommand(CanExecute = nameof(IsConnected))]
    private void Disconnect()
    {
        foreach (var entry in Devices.ToList())
            RemoveDeviceInternal(entry);
        Bus.Disconnect();
    }

    private void RestorePendingDevices()
    {
        if (_pendingDevices.Count == 0) return;
        foreach (var d in _pendingDevices)
        {
            var descriptor = Registry.Find(d.ModuleId);
            if (descriptor is not null) AddDeviceInstance(descriptor, d.NodeId, d.InstanceName);
        }
        _pendingDevices = [];
    }

    public void AddDeviceInstance(IDeviceModuleDescriptor descriptor, byte nodeId, string instanceName)
    {
        var context = new DeviceModuleContext { Bus = Bus, NodeId = nodeId, InstanceName = instanceName };
        var instance = descriptor.CreateInstance(context);
        var entry = new DeviceEntry(descriptor, instance);
        Devices.Add(entry);
        SelectedTabItem = entry;
        SaveSettings();
    }

    [RelayCommand]
    private void RemoveDevice(DeviceEntry? entry)
    {
        if (entry is null) return;
        RemoveDeviceInternal(entry);
        SaveSettings();
    }

    private void RemoveDeviceInternal(DeviceEntry entry)
    {
        Devices.Remove(entry);
        entry.Instance.Dispose();
    }

    private void SaveSettings()
    {
        AppSettingsService.Save(new AppSettings
        {
            TransportKind = SelectedTransportKind,
            Port = SelectedPort,
            Bitrate = SelectedBitrate,
            Devices = Devices.Select(d => new DeviceSettingsEntry
            {
                ModuleId = d.Descriptor.ModuleId,
                NodeId = d.Instance.NodeId,
                InstanceName = d.Instance.InstanceName,
            }).ToList(),
        });
    }

    public void Dispose()
    {
        foreach (var entry in Devices) entry.Instance.Dispose();
        Bus.Dispose();
    }
}
