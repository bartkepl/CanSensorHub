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

        (ChartSeriesGroups, TempSeries, VoltageSeries) = BuildChartSeries();
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

    #region Device info (GET_INFO)

    // Pola zgodne wstecz — wysyła je każda generacja firmware, pod stałymi przesunięciami.
    [ObservableProperty] private string _protocolVersionText = "—";
    [ObservableProperty] private string _fwVersionText = "—";
    // Pola rozszerzone — pojawiają się dopiero z ramką 21 B (układ info_layout warstwy wspólnej);
    // "—" znaczy tu "jeszcze nie odebrano", a ShowExtendedInfoNote — "węzeł tego nie zgłasza".
    [ObservableProperty] private string _hwVersionText = "—";
    [ObservableProperty] private string _buildRevisionText = "—";
    [ObservableProperty] private string _buildFlagsText = "—";
    [ObservableProperty] private string _uidText = "—";
    [ObservableProperty] private bool _hasExtendedInfo;
    // Pola profilu 2 — pojawiają się dopiero z ramką 28 B. Profil zgłaszany jest wprost, więc nie
    // wyprowadza się go z długości odpowiedzi, gdy węzeł go podaje.
    [ObservableProperty] private string _profileText = "—";
    [ObservableProperty] private string _deviceTypeText = "—";
    [ObservableProperty] private string _capabilitiesText = "—";
    [ObservableProperty] private bool _hasCommonProfileInfo;
    /// <summary>Niepusty, gdy węzeł zgłasza coś innego niż tablice <see cref="MpccInfo"/> tego modułu — odczyt komend mógł się rozejść. Pusty łańcuch = brak zastrzeżeń (służy zarazem jako źródło widoczności przez <see cref="HasFirmwareCompatWarning"/>).</summary>
    [ObservableProperty] private string _firmwareCompatWarning = "";

    public bool HasFirmwareCompatWarning => !string.IsNullOrEmpty(FirmwareCompatWarning);
    public bool ShowExtendedInfoNote => !HasExtendedInfo;
    public bool ShowCommonProfileNote => HasExtendedInfo && !HasCommonProfileInfo;

    partial void OnFirmwareCompatWarningChanged(string value) => OnPropertyChanged(nameof(HasFirmwareCompatWarning));
    partial void OnHasExtendedInfoChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowExtendedInfoNote));
        OnPropertyChanged(nameof(ShowCommonProfileNote));
    }

    partial void OnHasCommonProfileInfoChanged(bool value) => OnPropertyChanged(nameof(ShowCommonProfileNote));

    [RelayCommand] private Task RefreshInfo() => Client.GetInfoAsync();

    /// <summary>
    /// Buduje komunikat o niezgodności między węzłem a tablicami tego modułu. Pusty łańcuch oznacza
    /// brak zastrzeżeń.
    /// </summary>
    private static string BuildCompatWarning(MpccDeviceInfo info,
        bool protoMismatch, bool fwMajorMismatch, bool deviceTypeMismatch)
    {
        if (deviceTypeMismatch)
            return $"Węzeł zgłasza typ urządzenia 0x{info.DeviceType:X4}, a ten moduł obsługuje " +
                   $"0x{MpccInfo.DeviceType:X4}. Pod tym adresem pracuje urządzenie innego rodzaju — " +
                   "prezentowane wartości nie odnoszą się do sterownika zasilania.";

        if (protoMismatch)
            return $"Węzeł zgłasza wersję układu identyfikatora {info.ProtocolVersion}, a moduł " +
                   $"zbudowano dla {MpccInfo.ProtocolVersion}. Znaczenie pól ramki mogło ulec zmianie.";

        if (fwMajorMismatch)
        {
            var direction = info.FwMajor < MpccInfo.FwVersionMajor
                ? "Węzeł pracuje na starszej generacji firmware niż ta, dla której zbudowano moduł. " +
                  "Zgodność przywraca wgranie bieżącego obrazu przez narzędzie bootloadera."
                : "Węzeł pracuje na nowszej generacji firmware niż ta, dla której zbudowano moduł. " +
                  "Kształt niektórych komend mógł ulec zmianie.";
            return $"Węzeł zgłasza firmware {info.FwMajor}.{info.FwMinor}, a moduł zbudowano dla " +
                   $"{MpccInfo.FwVersionMajor}.{MpccInfo.FwVersionMinor}. {direction}";
        }

        return "";
    }

    private void OnInfo(object? sender, MpccDeviceInfo info)
    {
        IsOnline = true;
        FirmwareInfo = $"protokół v{info.ProtocolVersion}, firmware {info.FwMajor}.{info.FwMinor}, NODE=0x{info.Node:X2}";

        // Różnica wersji GŁÓWNEJ protokołu albo firmware oznacza ryzyko zmiany przełamującej —
        // konwencja wersjonowania device.yaml obiecuje zgodność wyłącznie w obrębie wersji
        // pobocznej → sygnalizowane jako krytyczne. Sama różnica numeru budowy jest z definicji
        // zgodna → odnotowywana informacyjnie i tylko wtedy, gdy jest jedyną różnicą.
        var protoMismatch = info.ProtocolVersion != MpccInfo.ProtocolVersion;
        var fwMajorMismatch = info.FwMajor != MpccInfo.FwVersionMajor;

        // Niezgodność typu urządzenia jest poważniejsza niż różnica wersji: oznacza, że pod tym
        // adresem pracuje urządzenie innego rodzaju, a jego telemetria i parametry zinterpretowane
        // według tablic sterownika zasilania dałyby wartości pozornie poprawne.
        var deviceTypeMismatch = info.DeviceTypeMismatch;

        var critical = protoMismatch || fwMajorMismatch || deviceTypeMismatch;
        var buildMismatch = info.HasExtendedInfo && info.BuildRevision != MpccInfo.BuildRevision;

        HeaderText = critical
            ? $"{InstanceName} — fw {info.FwMajor}.{info.FwMinor} (NODE=0x{info.Node:X2}) ⚠ NIEZGODNOŚĆ WERSJI"
            : $"{InstanceName} — fw {info.FwMajor}.{info.FwMinor} (NODE=0x{info.Node:X2})";

        ProtocolVersionText = info.ProtocolVersion.ToString();
        FwVersionText = $"{info.FwMajor}.{info.FwMinor}";
        HasExtendedInfo = info.HasExtendedInfo;
        if (info.HasExtendedInfo)
        {
            HwVersionText = $"{info.HwMajor}.{info.HwMinor}";
            BuildRevisionText = info.BuildRevision?.ToString() ?? "—";
            BuildFlagsText = DescribeBuildFlags(info.BuildFlags ?? MpccInfoBuildFlags.None);
            UidText = info.UidHex ?? "—";
        }

        // Numer profilu bierze się z pola PROFILE_VERSION, nie z pola PROTO_VERSION pokazywanego
        // wyżej. To pierwsze mówi, którą generacją wspólnej bazy posługuje się węzeł; to drugie —
        // którą wersją układu 29-bitowego identyfikatora. Rozdzielenie ich jest celowe: układ
        // identyfikatora pozostaje niezmieniony od początku, a profil zmienia się wraz z bazą.
        HasCommonProfileInfo = info.Profile >= DeviceProfile.Common;
        if (HasCommonProfileInfo)
        {
            ProfileText = info.ProfileVersion?.ToString() ?? "—";
            DeviceTypeText = info.DeviceType is { } deviceType
                ? $"0x{deviceType:X4}" + (deviceType == MpccInfo.DeviceType
                    ? " (MPCC)"
                    : " — nieobsługiwany przez ten moduł")
                : "—";
            CapabilitiesText = info.Capabilities is { } caps
                ? $"0x{caps:X8}   {DeviceCapabilityNames.Describe(caps)}"
                : "—";
        }

        FirmwareCompatWarning = BuildCompatWarning(info, protoMismatch, fwMajorMismatch, deviceTypeMismatch);

        if (critical)
        {
            AppendEventRow("SYSTEM", "-",
                $"⚠ NIEZGODNOŚĆ WERSJI: urządzenie zgłasza proto v{info.ProtocolVersion} fw {info.FwMajor}.{info.FwMinor}" +
                (info.HasExtendedInfo ? $" build #{info.BuildRevision}" : "") +
                $", ta aplikacja zbudowana dla proto v{MpccInfo.ProtocolVersion} fw {MpccInfo.FwVersionMajor}.{MpccInfo.FwVersionMinor} build #{MpccInfo.BuildRevision} — komendy/protokół mogą się różnić.");
        }
        else if (buildMismatch)
        {
            AppendEventRow("SYSTEM", "-",
                $"ℹ inny build firmware (#{info.BuildRevision} vs #{MpccInfo.BuildRevision} tej aplikacji) — ta sama wersja fw, drobna różnica buildu.");
        }
    }

    /// <summary>
    /// Brak ustawionych bitów oznacza Release — nieobecność flagi Debug jest dziś jedynym
    /// zdefiniowanym rodzajem budowy (<c>info_build_flags</c> warstwy wspólnej ma ten jeden bit).
    /// Ewentualne przyszłe bity są wypisywane zamiast być po cichu pomijane.
    /// </summary>
    private static string DescribeBuildFlags(MpccInfoBuildFlags flags)
    {
        if (flags == MpccInfoBuildFlags.None)
        {
            return "Release";
        }
        var parts = new List<string>();
        if (flags.HasFlag(MpccInfoBuildFlags.Debug))
        {
            parts.Add("DEBUG (nie używać w produkcji!)");
        }
        var unknown = flags & ~MpccInfoBuildFlags.Debug;
        if (unknown != MpccInfoBuildFlags.None)
        {
            parts.Add(unknown.ToString());
        }
        return string.Join(" · ", parts);
    }

    #endregion

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
        OnTelemetryForSensorPoll();
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
        if (t.Valid) RaiseChartSample(AverageSeriesKey(channel), tile.Value);
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

    /// <summary>
    /// Raised on every chart sample — averaged telemetry and individual-sensor readings alike — regardless
    /// of whether the curve is currently shown: the view buffers everything, so switching a curve on later
    /// still has history to display. <c>SeriesKey</c> identifies a <see cref="ChartSeriesVm"/>.
    /// </summary>
    public event EventHandler<(string SeriesKey, double Value)>? ChartSampleReceived;

    public event EventHandler? ChartsClearRequested;

    /// <summary>Side-panel groups: averaged channels first, then one group per sensor, one checkbox per curve.</summary>
    public IReadOnlyList<ChartSeriesGroupVm> ChartSeriesGroups { get; }
    /// <summary>Curves of the temperature plot, in legend order.</summary>
    public IReadOnlyList<ChartSeriesVm> TempSeries { get; }
    /// <summary>Curves of the ADC voltage plot, in legend order.</summary>
    public IReadOnlyList<ChartSeriesVm> VoltageSeries { get; }
    private readonly Dictionary<string, ChartSeriesVm> _chartSeriesByKey = [];

    public ChartSeriesVm? FindChartSeries(string key) => _chartSeriesByKey.GetValueOrDefault(key);

    public static string AverageSeriesKey(MpccChannel channel) => $"avg_{channel}";
    public static string SensorSeriesKey(MpccSensor sensor, MpccQuantity quantity) => $"{sensor}_{quantity}";

    /// <summary>
    /// One color per physical quantity, not per curve: an averaged channel and the sensor reading behind
    /// it are the same measurement, so they share a color and differ only in line style. Within one plot
    /// every quantity is distinct (Tableau 10 order), and the colors hold on a white plot background.
    /// Channel and quantity ids are the same global register, hence one table keyed by the byte value.
    /// </summary>
    private static readonly IReadOnlyDictionary<byte, string> QuantityColors = new Dictionary<byte, string>
    {
        [(byte)MpccQuantity.Temp] = "#D62728",
        [(byte)MpccQuantity.McuTemp] = "#1F77B4",
        [(byte)MpccQuantity.VoltageCh0] = "#1F77B4",
        [(byte)MpccQuantity.VoltageCh1] = "#FF7F0E",
        [(byte)MpccQuantity.VoltageCh2] = "#2CA02C",
        [(byte)MpccQuantity.VoltageCh3] = "#D62728",
        [(byte)MpccQuantity.VoltageCh4] = "#9467BD",
        [(byte)MpccQuantity.VoltageCh5] = "#8C564B",
        [(byte)MpccQuantity.VoltageCh6] = "#E377C2",
        [(byte)MpccQuantity.Vdda] = "#7F7F7F",
        [(byte)MpccQuantity.Vbat] = "#17BECF",
    };

    private (IReadOnlyList<ChartSeriesGroupVm> Groups, IReadOnlyList<ChartSeriesVm> Temp, IReadOnlyList<ChartSeriesVm> Voltage) BuildChartSeries()
    {
        var groups = new List<ChartSeriesGroupVm>();

        // Telemetry channels are shown by default: they arrive on every TELEMETRY_PERIOD without polling.
        // MPCC sends the latest sample there, not an average, hence the "(telemetria)" label.
        var averages = new List<ChartSeriesVm>();
        foreach (var channel in Enum.GetValues<MpccChannel>())
        {
            var target = ChartTargetFor((MpccQuantity)channel);
            if (target == ChartTarget.None) continue;
            var label = MpccTables.Channels[channel].Label;
            averages.Add(new ChartSeriesVm
            {
                Key = AverageSeriesKey(channel), Label = label, LegendLabel = $"{label} (telemetria)",
                Target = target, ColorHex = QuantityColors[(byte)channel], IsAverage = true, IsChecked = true,
            });
        }
        groups.Add(new ChartSeriesGroupVm("Telemetria", averages));

        foreach (var (sensor, quantities) in MpccTables.SensorProvides)
        {
            var sensorName = MpccTables.SensorNames[sensor];
            var shortName = sensorName.Split(" (")[0];
            var series = new List<ChartSeriesVm>();
            foreach (var q in quantities)
            {
                var target = ChartTargetFor(q);
                if (target == ChartTarget.None) continue;
                var label = MpccTables.Quantities[q].Label;
                series.Add(new ChartSeriesVm
                {
                    Key = SensorSeriesKey(sensor, q), Label = label, LegendLabel = $"{shortName}: {label}",
                    Target = target, ColorHex = QuantityColors[(byte)q],
                });
            }
            if (series.Count > 0) groups.Add(new ChartSeriesGroupVm(sensorName, series));
        }

        foreach (var s in groups.SelectMany(g => g.Series)) _chartSeriesByKey[s.Key] = s;
        var all = groups.SelectMany(g => g.Series).ToList();
        return (groups, all.Where(s => s.Target == ChartTarget.Temp).ToList(), all.Where(s => s.Target == ChartTarget.Voltage).ToList());
    }

    private void RaiseChartSample(string seriesKey, double value)
    {
        if (_chartSeriesByKey.TryGetValue(seriesKey, out var series)) series.HasData = true;
        ChartSampleReceived?.Invoke(this, (seriesKey, value));
    }

    [ObservableProperty] private bool _autoPollSensors;
    private System.Threading.Timer? _sensorPollTimer;

    // Odpytanie czujników jest wyzwalane nadejściem telemetrii, a nie niezależnym zegarem. Obie
    // drogi są migawkami wartości odświeżanej co MEASURE_PERIOD i stemplowanymi chwilą odbioru;
    // przy niezależnych zegarach węzła i komputera odczyt czujnika trafiał w dowolną fazę względem
    // telemetrii i na wykresie był przesunięty o ułamek sekundy albo pochodził z sąsiedniego
    // przebiegu pomiarowego. Zapytanie wysłane zaraz po paczce telemetrii trafia zwykle w ten sam
    // przebieg. Zegar pozostaje zapasowy — odpytuje tylko wtedy, gdy telemetria nie nadchodzi.
    private const long TelemetryPollMinIntervalMs = 1500;
    private const long FallbackPollIntervalMs = 2000;
    private const long TelemetrySilenceMs = 3000;
    private long _lastSensorPollMs;
    private long _lastTelemetryMs;

    partial void OnAutoPollSensorsChanged(bool value)
    {
        if (value)
        {
            Interlocked.Exchange(ref _lastSensorPollMs, 0);
            _sensorPollTimer ??= new System.Threading.Timer(_ =>
            {
                var now = Environment.TickCount64;
                if (now - Interlocked.Read(ref _lastTelemetryMs) > TelemetrySilenceMs)
                    PollSensorsIfDue(FallbackPollIntervalMs);
            }, null, TimeSpan.Zero, TimeSpan.FromMilliseconds(500));
        }
        else
        {
            _sensorPollTimer?.Dispose();
            _sensorPollTimer = null;
        }
    }

    private void OnTelemetryForSensorPoll()
    {
        Interlocked.Exchange(ref _lastTelemetryMs, Environment.TickCount64);
        // Pierwsza ramka paczki wyzwala odpytanie; minimalny odstęp tłumi pozostałe kanały tej
        // samej paczki, a przy krótkim TELEMETRY_PERIOD ogranicza obciążenie magistrali.
        if (AutoPollSensors) PollSensorsIfDue(TelemetryPollMinIntervalMs);
    }

    // Wywoływane równolegle z wątku odbioru magistrali i z zegara zapasowego — o tym, kto
    // wysyła odpytanie, rozstrzyga CompareExchange, więc jedna chwila daje co najwyżej jedno.
    private void PollSensorsIfDue(long minIntervalMs)
    {
        var now = Environment.TickCount64;
        var last = Interlocked.Read(ref _lastSensorPollMs);
        if (last != 0 && now - last < minIntervalMs) return;
        if (Interlocked.CompareExchange(ref _lastSensorPollMs, now, last) != last) return;
        foreach (var s in Enum.GetValues<MpccSensor>()) _ = Client.ReadSensorAsync(s);
    }

    [RelayCommand]
    private void ClearCharts()
    {
        foreach (var s in _chartSeriesByKey.Values) s.HasData = false;
        ChartsClearRequested?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void SelectAllSensorTraces()
    {
        foreach (var s in _chartSeriesByKey.Values) s.IsChecked = true;
    }

    [RelayCommand]
    private void DeselectAllSensorTraces()
    {
        foreach (var s in _chartSeriesByKey.Values) s.IsChecked = false;
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

        foreach (var reading in e.Readings)
            RaiseChartSample(SensorSeriesKey(e.Sensor, reading.Quantity), reading.Value);
    }

    /// <summary>Maps a per-sensor quantity onto whichever existing averaged-channel plot shares its physical unit.</summary>
    private static ChartTarget ChartTargetFor(MpccQuantity q) => q switch
    {
        MpccQuantity.Temp or MpccQuantity.McuTemp => ChartTarget.Temp,
        MpccQuantity.Vdda or MpccQuantity.Vbat
            or MpccQuantity.VoltageCh0 or MpccQuantity.VoltageCh1 or MpccQuantity.VoltageCh2
            or MpccQuantity.VoltageCh3 or MpccQuantity.VoltageCh4 or MpccQuantity.VoltageCh5
            or MpccQuantity.VoltageCh6 => ChartTarget.Voltage,
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
        nameof(MpccEventCode.SensorAlert) => "Alert progowy STS31",
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
