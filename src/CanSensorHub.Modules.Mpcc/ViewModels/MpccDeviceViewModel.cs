using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CanSensorHub.Core.Can;
using CanSensorHub.Core.Modules;
using CanSensorHub.Core.Protocol;
using CanSensorHub.Modules.Mpcc.Protocol;
using CanSensorHub.Modules.Mpcc.Views;
using Microsoft.Win32;

namespace CanSensorHub.Modules.Mpcc.ViewModels;

/// <summary>Which existing averaged-channel plot an individual sensor quantity should be overlaid onto.</summary>
public enum ChartTarget { None, Temp, Voltage }

/// <summary>
/// Root view model for one MPCC device instance: owns the typed <see cref="MpccDeviceClient"/> and every
/// tab's state (dashboard, charts, output/LED control, parameters, sensors, subsystem status, RTC,
/// events). Bootloader flashing lives at the shell level now (identical protocol for every module — see
/// CanSensorHub.App's Bootloader tool). One class per module keeps file count manageable — the tabs are
/// still cleanly separated in the XAML and in code via #region.
/// </summary>
public partial class MpccDeviceViewModel : ObservableObject, IDeviceModuleInstance
{
    private readonly IDisposable? _simulator;

    public MpccDeviceClient Client { get; }
    public byte NodeId => Client.NodeId;
    public string InstanceName { get; }
    public MpccDeviceView RootView { get; }
    // Implicit (not explicit) interface implementation on purpose: WPF's reflection-based data binding
    // (MainWindow's "{Binding Instance.View}") resolves members against the runtime CLR type, and can't
    // see explicit interface implementations there.
    public object View => RootView;

    [ObservableProperty] private string _headerText;
    [ObservableProperty] private bool _isOnline;
    [ObservableProperty] private string _firmwareInfo = "brak danych (wyślij GET_INFO)";

    public event EventHandler? HeaderChanged;

    public MpccDeviceViewModel(DeviceModuleContext context, IDisposable? simulator)
    {
        _simulator = simulator;
        InstanceName = context.InstanceName;
        _headerText = $"{InstanceName} (NODE=0x{context.NodeId:X2})";

        Client = new MpccDeviceClient(context.Bus, context.NodeId);
        Client.TelemetryReceived += OnTelemetry;
        Client.ResponseReceived += OnResponse;
        Client.EventReceived += OnEvent;
        Client.SensorsListReceived += OnSensorsList;
        Client.SensorReadingsReceived += OnSensorReadings;
        Client.InfoReceived += OnInfo;
        Client.DeviceStatusReceived += OnDeviceStatus;
        Client.TimeReceived += OnTime;
        Client.Error += (_, msg) => AppendEventRow("ERROR", "-", msg);

        InitDashboard();
        InitParams();
        InitSensors();
        InitStatusFlags();

        RootView = new MpccDeviceView { DataContext = this };

        _ = Client.GetInfoAsync();
        _ = Client.GetStatusAsync();
        _ = Client.ListSensorsAsync();
    }

    partial void OnHeaderTextChanged(string value) => HeaderChanged?.Invoke(this, EventArgs.Empty);
    partial void OnIsOnlineChanged(bool value) => HeaderChanged?.Invoke(this, EventArgs.Empty);

    private void OnInfo(object? sender, MpccDeviceInfo info)
    {
        IsOnline = true;
        FirmwareInfo = $"protokół v{info.ProtocolVersion}, firmware {info.FwMajor}.{info.FwMinor}, NODE=0x{info.Node:X2}";
        HeaderText = $"{InstanceName} — fw {info.FwMajor}.{info.FwMinor} (NODE=0x{info.Node:X2})";
    }

    #region Dashboard

    public ObservableCollection<ChannelTileVm> DashboardTiles { get; } = [];
    [ObservableProperty] private bool _isRecordingCsv;
    [ObservableProperty] private string? _csvPath;
    public string RecordButtonLabel => IsRecordingCsv ? "Zatrzymaj nagrywanie" : "Rozpocznij nagrywanie";
    partial void OnIsRecordingCsvChanged(bool value) => OnPropertyChanged(nameof(RecordButtonLabel));

    private void InitDashboard()
    {
        CsvPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            $"mpcc_{Client.NodeId:X2}_{DateTime.Now:yyyyMMdd_HHmmss}.csv");
        foreach (var channel in Enum.GetValues<MpccChannel>())
        {
            var info = MpccTables.Channels[channel];
            DashboardTiles.Add(new ChannelTileVm { Name = info.Label, Unit = info.Unit });
        }
    }

    private void OnTelemetry(object? sender, TelemetryRaw t)
    {
        if (!Enum.IsDefined(typeof(MpccChannel), t.Channel)) return;
        var channel = (MpccChannel)t.Channel;
        var info = MpccTables.Channels[channel];
        var idx = Array.IndexOf(Enum.GetValues<MpccChannel>(), channel);
        if (idx < 0 || idx >= DashboardTiles.Count) return;
        var tile = DashboardTiles[idx];
        tile.IsValid = t.Valid;
        if (t.Valid)
        {
            tile.Value = t.Value * info.Scale;
            tile.LastUpdate = DateTimeOffset.Now;
        }
        ChartSampleReceived?.Invoke(this, (channel, tile.Value, t.Valid));
    }

    [RelayCommand]
    private void BrowseCsvPath()
    {
        var dlg = new SaveFileDialog
        {
            Filter = "Pliki CSV (*.csv)|*.csv",
            FileName = string.IsNullOrWhiteSpace(CsvPath) ? $"mpcc_{Client.NodeId:X2}_{DateTime.Now:yyyyMMdd_HHmmss}.csv" : Path.GetFileName(CsvPath),
        };
        if (dlg.ShowDialog() == true) CsvPath = dlg.FileName;
    }

    [RelayCommand]
    private void ToggleCsvRecording()
    {
        if (IsRecordingCsv)
        {
            Client.StopCsvLogging();
            IsRecordingCsv = false;
            return;
        }
        if (string.IsNullOrWhiteSpace(CsvPath))
        {
            BrowseCsvPath();
            if (string.IsNullOrWhiteSpace(CsvPath)) return;
        }
        Client.StartCsvLogging(CsvPath, TimeSpan.FromSeconds(5));
        IsRecordingCsv = true;
    }

    #endregion

    #region Charts

    /// <summary>Raised on every telemetry (fused/averaged) sample so the Charts view's code-behind can feed ScottPlot.</summary>
    public event EventHandler<(MpccChannel Channel, double Value, bool Valid)>? ChartSampleReceived;

    /// <summary>
    /// Raised on every individual-sensor sample, regardless of whether its sensor's checkbox is currently
    /// checked (the view buffers everything so toggling a box on later still has data to show) — routed
    /// to whichever existing averaged-channel plot matches its physical quantity, per <see cref="ChartTarget"/>.
    /// SensorLabel/QuantityLabel are kept separate (rather than pre-joined) so the view can drop the
    /// quantity suffix when a sensor contributes only one curve to a given plot — the axis title/legend
    /// already says what's being measured there, no need to repeat it on every single curve.
    /// </summary>
    public event EventHandler<(string SensorKey, string SensorLabel, ChartTarget Target, string SeriesKey, string QuantityLabel, double Value)>? SensorChartSampleReceived;

    public event EventHandler? ChartsClearRequested;

    /// <summary>One checkbox per SENSOR (not per quantity) — checking "MCU" plots temp+VDDA+VBAT together, matching the reference app's per-sensor curves.</summary>
    public ObservableCollection<SensorChartToggleVm> SensorToggles { get; } = [];
    private readonly Dictionary<string, SensorChartToggleVm> _sensorTogglesByKey = [];

    [ObservableProperty] private bool _autoPollSensors;
    private System.Threading.Timer? _sensorPollTimer;

    partial void OnAutoPollSensorsChanged(bool value)
    {
        if (value)
        {
            _sensorPollTimer ??= new System.Threading.Timer(_ =>
            {
                foreach (var s in Enum.GetValues<MpccSensor>()) _ = Client.ReadSensorAsync(s);
            }, null, TimeSpan.Zero, TimeSpan.FromSeconds(2));
        }
        else
        {
            _sensorPollTimer?.Dispose();
            _sensorPollTimer = null;
        }
    }

    [RelayCommand]
    private void ClearCharts() => ChartsClearRequested?.Invoke(this, EventArgs.Empty);

    [RelayCommand]
    private void SelectAllSensorTraces()
    {
        foreach (var t in SensorToggles) t.IsChecked = true;
    }

    [RelayCommand]
    private void DeselectAllSensorTraces()
    {
        foreach (var t in SensorToggles) t.IsChecked = false;
    }

    #endregion

    #region Control (output / LED / single ADC read)

    [ObservableProperty] private bool? _outputState;
    [ObservableProperty] private bool _ledR;
    [ObservableProperty] private bool _ledG;
    [ObservableProperty] private bool _ledB;
    [ObservableProperty] private int _adcChannel;
    [ObservableProperty] private string _adcReadout = "—";

    [RelayCommand] private Task OutputOn() => Client.SetOutputAsync(true);
    [RelayCommand] private Task OutputOff() => Client.SetOutputAsync(false);
    [RelayCommand] private Task RefreshOutput() => Client.GetOutputAsync();
    [RelayCommand] private Task ApplyLed() => Client.SetLedAsync(LedR, LedG, LedB);
    [RelayCommand] private Task ReadSingleAdc() => Client.ReadAdcAsync((byte)AdcChannel);

    #endregion

    #region Params

    /// <summary>Flat list; the Params view groups it by <see cref="ParamDescriptor.Group"/> via a CollectionViewSource.</summary>
    public ObservableCollection<ParamEditRowVm> Params { get; } = [];
    private readonly Dictionary<byte, ParamEditRowVm> _paramById = [];

    private void InitParams()
    {
        foreach (var p in MpccParams.All)
        {
            var row = new ParamEditRowVm(p, WriteParamAsync);
            _paramById[p.Id] = row;
            Params.Add(row);
        }
    }

    private async Task WriteParamAsync(ParamDescriptor descriptor, double value) => await Client.WriteParamAsync(descriptor, value);

    [RelayCommand]
    private async Task ReadAllParams()
    {
        foreach (var p in MpccParams.All)
        {
            await Client.ReadParamAsync(p.Id);
            await Task.Delay(15); // przerwa między ramkami — nie zalewamy magistrali
        }
    }

    [RelayCommand] private Task SaveConfig() => Client.SaveConfigAsync();

    [RelayCommand]
    private async Task LoadDefaults()
    {
        if (MessageBox.Show("Przywrócić domyślne wartości wszystkich parametrów na urządzeniu?", "Wartości domyślne",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        await Client.LoadDefaultsAsync();
        await ReadAllParams();
    }

    private void OnResponse(object? sender, ResponseMessage r)
    {
        if (r.Opcode == (byte)MpccReqOp.ReadParam && r.Status == StatusCode.Ok && r.Data.Length > 0 && _paramById.TryGetValue(r.Arg, out var row))
        {
            row.ApplyDeviceValue(ParamCodec.Decode(row.Descriptor.Type, r.Data));
        }
        else if (r.Opcode == (byte)MpccReqOp.GetOutput && r.Status == StatusCode.Ok && r.Data.Length > 0)
        {
            OutputState = r.Data[0] != 0;
        }
        else if (r.Opcode == (byte)MpccReqOp.SetOutput && r.Status == StatusCode.Ok)
        {
            _ = Client.GetOutputAsync();
        }
        else if (r.Opcode == (byte)MpccReqOp.ReadAdc && r.Status == StatusCode.Ok && r.Data.Length >= 4)
        {
            AdcReadout = $"{BitConverter.ToInt32(r.Data, 0) * 0.001:F3} V";
        }
        else if (r.Opcode == (byte)MpccReqOp.GetTime && r.Status != StatusCode.Ok)
        {
            AppendEventRow("RESPONSE", MpccReqOp.GetTime.ToString(), r.Status.ToString());
        }
    }

    #endregion

    #region Sensors

    public ObservableCollection<SensorRowVm> Sensors { get; } = [];
    private readonly Dictionary<byte, SensorRowVm> _sensorRows = [];

    private void InitSensors()
    {
        foreach (var s in Enum.GetValues<MpccSensor>())
        {
            var sensor = s;
            var row = new SensorRowVm(MpccTables.SensorNames[s], () => Client.ReadSensorAsync(sensor));
            _sensorRows[(byte)s] = row;
            Sensors.Add(row);
        }
    }

    [RelayCommand]
    private async Task RefreshAllSensors()
    {
        await Client.ListSensorsAsync();
        foreach (var s in Enum.GetValues<MpccSensor>())
        {
            await Client.ReadSensorAsync(s);
            await Task.Delay(15);
        }
    }

    private void OnSensorsList(object? sender, IReadOnlyDictionary<byte, byte> map)
    {
        foreach (var (id, status) in map)
        {
            if (!_sensorRows.TryGetValue(id, out var row)) continue;
            row.Status = status switch { 0 => "obecny", 1 => "nieobecny", 2 => "awaria", 3 => "wyłączony", _ => $"0x{status:X2}" };
        }
    }

    private void OnSensorReadings(object? sender, (MpccSensor Sensor, IReadOnlyList<MpccSensorReading> Readings) e)
    {
        if (_sensorRows.TryGetValue((byte)e.Sensor, out var row))
        {
            row.LastReading = e.Readings.Count == 0 ? "—" : string.Join(", ", e.Readings.Select(r => $"{r.Value:F2} {r.Unit}"));
            row.LastUpdate = DateTimeOffset.Now;
        }

        var sensorKey = e.Sensor.ToString();
        if (!_sensorTogglesByKey.TryGetValue(sensorKey, out var toggle))
        {
            toggle = new SensorChartToggleVm { Key = sensorKey, Label = MpccTables.SensorNames[e.Sensor] };
            _sensorTogglesByKey[sensorKey] = toggle;
            SensorToggles.Add(toggle);
        }

        foreach (var reading in e.Readings)
        {
            var target = ChartTargetFor(reading.Quantity);
            if (target == ChartTarget.None) continue;
            var seriesKey = $"{sensorKey}_{reading.Quantity}";
            SensorChartSampleReceived?.Invoke(this, (sensorKey, toggle.Label, target, seriesKey, MpccTables.Quantities[reading.Quantity].Label, reading.Value));
        }
    }

    /// <summary>Maps a per-sensor quantity onto whichever existing averaged-channel plot shares its physical unit.</summary>
    private static ChartTarget ChartTargetFor(MpccQuantity q) => q switch
    {
        MpccQuantity.Temp or MpccQuantity.McuTemp => ChartTarget.Temp,
        MpccQuantity.Vdda or MpccQuantity.Vbat or MpccQuantity.AdcV => ChartTarget.Voltage,
        _ => ChartTarget.None,
    };

    #endregion

    #region Status

    public ObservableCollection<FlagIndicatorVm> SysFlagIndicators { get; } = [];
    public ObservableCollection<FlagIndicatorVm> ErrFlagIndicators { get; } = [];
    [ObservableProperty] private bool _isCritical;
    [ObservableProperty] private int _healthySensorCount;

    private void InitStatusFlags()
    {
        foreach (var f in Enum.GetValues<DeviceSysFlags>())
        {
            if (f == DeviceSysFlags.None) continue;
            SysFlagIndicators.Add(new FlagIndicatorVm { Name = f.ToString(), Description = DescribeSysFlag(f) });
        }
        foreach (var f in Enum.GetValues<DeviceErrFlags>())
        {
            if (f == DeviceErrFlags.None) continue;
            ErrFlagIndicators.Add(new FlagIndicatorVm { Name = f.ToString(), Description = DescribeErrFlag(f), IsDanger = true });
        }
    }

    [RelayCommand] private Task RefreshStatus() => Client.GetStatusAsync();

    [RelayCommand]
    private async Task ResetDevice()
    {
        if (MessageBox.Show("Zresetować urządzenie?", "Reset", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        await Client.ResetAsync();
    }

    private void OnDeviceStatus(object? sender, DeviceStatusReport st)
    {
        foreach (var ind in SysFlagIndicators)
            ind.IsSet = st.SysFlags.HasFlag(Enum.Parse<DeviceSysFlags>(ind.Name));
        foreach (var ind in ErrFlagIndicators)
            ind.IsSet = st.ErrFlags.HasFlag(Enum.Parse<DeviceErrFlags>(ind.Name));
        IsCritical = st.Critical;
        HealthySensorCount = st.NHealthy;
    }

    private static string DescribeSysFlag(DeviceSysFlags f) => f switch
    {
        DeviceSysFlags.LseActive => "Zegar LSE + autotrim MSI aktywny",
        DeviceSysFlags.RtcOk => "RTC (MCP79410) obecny i pracuje",
        DeviceSysFlags.CanOk => "Sterownik CAN zainicjowany i pracuje",
        DeviceSysFlags.I2cOk => "Magistrala I2C zainicjowana i pracuje",
        DeviceSysFlags.AdcOk => "Wewnętrzny ADC MCU sprawny",
        DeviceSysFlags.FpuOk => "FPU włączone",
        DeviceSysFlags.ConfigOk => "Konfiguracja załadowana i ważna",
        DeviceSysFlags.SensorsOk => "Przynajmniej jeden czujnik dostarcza dane",
        _ => "",
    };

    private static string DescribeErrFlag(DeviceErrFlags f) => f switch
    {
        DeviceErrFlags.Critical => "Błąd krytyczny aktywny (brak czujnika temperatury lub awaria FPU)",
        DeviceErrFlags.SensorFault => "Co najmniej jeden czujnik w awarii",
        DeviceErrFlags.NoSensors => "Czujnik temperatury nie dostarcza danych",
        DeviceErrFlags.WdtReset => "Ostatni reset pochodził od watchdoga",
        DeviceErrFlags.NvmError => "Błąd zapisu/odczytu pamięci nieulotnej",
        DeviceErrFlags.CanBusoff => "Wystąpił i został odzyskany bus-off CAN",
        DeviceErrFlags.I2cRecovered => "Wykonano odzysk zawieszonej magistrali I2C",
        DeviceErrFlags.VbatLow => "Napięcie VBAT poniżej progu (~2,4 V)",
        _ => "",
    };

    #endregion

    #region Time

    [ObservableProperty] private string _deviceTimeText = "—";
    [ObservableProperty] private DateTime _manualTime = DateTime.Now;

    [RelayCommand] private Task ReadTime() => Client.GetTimeAsync();

    [RelayCommand]
    private Task SetTimeFromPc()
    {
        var now = DateTime.Now;
        return Client.SetTimeAsync(now.Second, now.Minute, now.Hour, now.Day, now.Month, now.Year, (int)now.DayOfWeek + 1);
    }

    [RelayCommand]
    private Task SetTimeManual() => Client.SetTimeAsync(ManualTime.Second, ManualTime.Minute, ManualTime.Hour,
        ManualTime.Day, ManualTime.Month, ManualTime.Year, (int)ManualTime.DayOfWeek + 1);

    private void OnTime(object? sender, MpccRtcTime t) =>
        DeviceTimeText = $"{t.Year:D4}-{t.Month:D2}-{t.Day:D2} {t.Hour:D2}:{t.Minute:D2}:{t.Second:D2}";

    #endregion

    #region Events

    public ObservableCollection<EventLogRowVm> EventLog { get; } = [];
    private const int MaxEventRows = 2000;

    private void OnEvent(object? sender, EventMessage e)
    {
        var code = Enum.IsDefined(typeof(MpccEventCode), e.Code) ? ((MpccEventCode)e.Code).ToString() : $"0x{e.Code:X2}";
        var source = e.Source == MpccEventSource.System ? "SYSTEM"
            : Enum.IsDefined(typeof(MpccSensor), e.Source) ? MpccTables.SensorNames[(MpccSensor)e.Source] : $"0x{e.Source:X2}";
        AppendEventRow(source, code, string.Join(' ', e.Data.Select(b => b.ToString("X2"))));
    }

    private void AppendEventRow(string source, string code, string data)
    {
        EventLog.Insert(0, new EventLogRowVm(DateTimeOffset.Now, source, code, data, DescribeEvent(code)));
        while (EventLog.Count > MaxEventRows) EventLog.RemoveAt(EventLog.Count - 1);
    }

    private static string DescribeEvent(string code) => code switch
    {
        nameof(MpccEventCode.Boot) => "Start węzła",
        nameof(MpccEventCode.Sts31Alert) => "Alert progowy STS31",
        nameof(MpccEventCode.SensorFault) => "Awaria czujnika",
        nameof(MpccEventCode.SensorBack) => "Czujnik wrócił do sprawności",
        nameof(MpccEventCode.BusRecovered) => "Odzysk magistrali / I2C",
        nameof(MpccEventCode.Heartbeat) => "Diagnostyka cykliczna",
        nameof(MpccEventCode.SensorIrq) => "Przerwanie PCF8574",
        nameof(MpccEventCode.CriticalAlarm) => "BŁĄD KRYTYCZNY",
        nameof(MpccEventCode.Button) => "Zdarzenie przycisku",
        nameof(MpccEventCode.OutputChanged) => "Zmiana stanu wyjścia OD",
        _ => "",
    };

    #endregion

    public void Dispose()
    {
        _sensorPollTimer?.Dispose();
        Client.Dispose();
        _simulator?.Dispose();
    }
}
