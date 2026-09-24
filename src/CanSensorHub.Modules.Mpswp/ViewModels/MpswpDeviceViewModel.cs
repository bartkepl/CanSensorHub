using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CanSensorHub.Core.Modules;
using CanSensorHub.Core.Protocol;
using CanSensorHub.Modules.Mpswp.Protocol;
using CanSensorHub.Modules.Mpswp.Views;
using Microsoft.Win32;

namespace CanSensorHub.Modules.Mpswp.ViewModels;

/// <summary>Which existing averaged-channel plot an individual sensor quantity should be overlaid onto.</summary>
public enum ChartTarget { None, Temp, Hum, Press, Air }

/// <summary>
/// Root view model for one MPSWP device instance: owns the typed <see cref="MpswpDeviceClient"/> and
/// every tab's state (dashboard, charts, parameters, sensors + AS3935 antenna calibration, subsystem
/// status, RTC, events). Bootloader flashing lives at the shell level now (identical protocol for every
/// module — see CanSensorHub.App's Bootloader tool). One class per module keeps file count manageable —
/// tabs stay cleanly separated in the XAML and via #region here.
/// </summary>
public partial class MpswpDeviceViewModel : ObservableObject, IDeviceModuleInstance
{
    private readonly IDisposable? _simulator;

    public MpswpDeviceClient Client { get; }
    public byte NodeId => Client.NodeId;
    public string InstanceName { get; }
    public MpswpDeviceView RootView { get; }
    // Implicit (not explicit) interface implementation on purpose: WPF's reflection-based data binding
    // (MainWindow's "{Binding Instance.View}") resolves members against the runtime CLR type, and can't
    // see explicit interface implementations there.
    public object View => RootView;

    [ObservableProperty] private string _headerText;
    [ObservableProperty] private bool _isOnline;
    [ObservableProperty] private string _firmwareInfo = "brak danych (wyślij GET_INFO)";

    public event EventHandler? HeaderChanged;

    public MpswpDeviceViewModel(DeviceModuleContext context, IDisposable? simulator)
    {
        _simulator = simulator;
        InstanceName = context.InstanceName;
        _headerText = $"{InstanceName} (NODE=0x{context.NodeId:X2})";

        Client = new MpswpDeviceClient(context.Bus, context.NodeId);
        Client.TelemetryReceived += OnTelemetry;
        Client.ResponseReceived += OnResponse;
        Client.EventReceived += OnEvent;
        Client.SensorsListReceived += OnSensorsList;
        Client.SensorReadingsReceived += OnSensorReadings;
        Client.InfoReceived += OnInfo;
        Client.DeviceStatusReceived += OnDeviceStatus;
        Client.TimeReceived += OnTime;
        Client.AntennaCalibrated += OnAntennaCalibrated;
        Client.SensorRangeChanged += OnSensorRangeChanged;
        Client.Error += (_, msg) => AppendEventRow("ERROR", "-", msg);

        (ChartSeriesGroups, var allSeries) = BuildChartSeries();
        TempSeries = allSeries.Where(s => s.Target == ChartTarget.Temp).ToList();
        HumSeries = allSeries.Where(s => s.Target == ChartTarget.Hum).ToList();
        PressSeries = allSeries.Where(s => s.Target == ChartTarget.Press).ToList();
        AirSeries = allSeries.Where(s => s.Target == ChartTarget.Air).ToList();
        InitDashboard();
        InitParams();
        InitSensors();
        InitStatusFlags();

        RootView = new MpswpDeviceView { DataContext = this };

        _ = Client.GetInfoAsync();
        _ = Client.GetStatusAsync();
        _ = Client.ListSensorsAsync();
    }

    partial void OnHeaderTextChanged(string value) => HeaderChanged?.Invoke(this, EventArgs.Empty);
    partial void OnIsOnlineChanged(bool value) => HeaderChanged?.Invoke(this, EventArgs.Empty);

    #region Device info (GET_INFO)

    // Legacy fields — every firmware generation sends these, at stable offsets.
    [ObservableProperty] private string _protocolVersionText = "—";
    [ObservableProperty] private string _fwVersionText = "—";
    // Extended fields — only present once GET_INFO's 21 B frame lands (protocol.yaml's info_layout);
    // "—" here just means "not yet received", ShowExtendedInfoNote tells "device doesn't send this".
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
    /// <summary>Non-empty when the connected device's FwMajor.FwMinor is newer than <see cref="MpswpInfo"/> — this app's protocol port may be reading some changed command wrong. Empty string = no warning (also doubles as its own Visibility source via <see cref="HasFirmwareCompatWarning"/>).</summary>
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
    private static string BuildCompatWarning(MpswpDeviceInfo info,
        bool protoMismatch, bool fwMajorMismatch, bool deviceTypeMismatch)
    {
        if (deviceTypeMismatch)
            return $"Węzeł zgłasza typ urządzenia 0x{info.DeviceType:X4}, a ten moduł obsługuje " +
                   $"0x{MpswpInfo.DeviceType:X4}. Pod tym adresem pracuje urządzenie innego rodzaju — " +
                   "prezentowane wartości nie odnoszą się do stacji pogodowej.";

        if (protoMismatch)
            return $"Węzeł zgłasza wersję układu identyfikatora {info.ProtocolVersion}, a moduł " +
                   $"zbudowano dla {MpswpInfo.ProtocolVersion}. Znaczenie pól ramki mogło ulec zmianie.";

        if (fwMajorMismatch)
        {
            var direction = info.FwMajor < MpswpInfo.FwVersionMajor
                ? "Węzeł pracuje na starszej generacji firmware niż ta, dla której zbudowano moduł. " +
                  "Zgodność przywraca wgranie bieżącego obrazu przez narzędzie bootloadera."
                : "Węzeł pracuje na nowszej generacji firmware niż ta, dla której zbudowano moduł. " +
                  "Kształt niektórych komend mógł ulec zmianie.";
            return $"Węzeł zgłasza firmware {info.FwMajor}.{info.FwMinor}, a moduł zbudowano dla " +
                   $"{MpswpInfo.FwVersionMajor}.{MpswpInfo.FwVersionMinor}. {direction}";
        }

        return "";
    }

    private void OnInfo(object? sender, MpswpDeviceInfo info)
    {
        IsOnline = true;
        FirmwareInfo = $"protokół v{info.ProtocolVersion}, firmware {info.FwMajor}.{info.FwMinor}, NODE=0x{info.Node:X2}";

        // Mirrors the reference Python app's controller.py/app.py: proto or fw MAJOR differing from what
        // this port was built against is a breaking-change risk (protocol.yaml's own versioning
        // convention only promises compatibility across MINOR bumps) → critical, visible everywhere.
        // A build-revision-only difference (same fw major.minor) is compatible by definition → info only,
        // and only reported when it's the ONLY difference (elif in the reference — critical already says enough).
        var protoMismatch = info.ProtocolVersion != MpswpInfo.ProtocolVersion;
        var fwMajorMismatch = info.FwMajor != MpswpInfo.FwVersionMajor;

        // Niezgodność typu urządzenia jest poważniejsza niż różnica wersji: oznacza, że pod tym
        // adresem pracuje urządzenie innego rodzaju, a jego telemetria i parametry zinterpretowane
        // według tablic stacji pogodowej dałyby wartości pozornie poprawne.
        var deviceTypeMismatch = info.DeviceTypeMismatch;

        var critical = protoMismatch || fwMajorMismatch || deviceTypeMismatch;
        var buildMismatch = info.HasExtendedInfo && info.BuildRevision != MpswpInfo.BuildRevision;

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
            BuildFlagsText = DescribeBuildFlags(info.BuildFlags ?? MpswpInfoBuildFlags.None);
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
                ? $"0x{deviceType:X4}" + (deviceType == MpswpInfo.DeviceType
                    ? " (MPSWP)"
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
                $", ta aplikacja zbudowana dla proto v{MpswpInfo.ProtocolVersion} fw {MpswpInfo.FwVersionMajor}.{MpswpInfo.FwVersionMinor} build #{MpswpInfo.BuildRevision} — komendy/protokół mogą się różnić.");
        }
        else if (buildMismatch)
        {
            AppendEventRow("SYSTEM", "-",
                $"ℹ inny build firmware (#{info.BuildRevision} vs #{MpswpInfo.BuildRevision} tej aplikacji) — ta sama wersja fw, drobna różnica buildu.");
        }
    }

    /// <summary>
    /// No bits set means Release (the absence of the Debug flag is the only defined "build type" today —
    /// protocol.yaml's info_build_flags has just the one bit). Previously mapped None to "—", which read as
    /// "unknown" rather than "confirmed Release build". Also surfaces any future flag bits (added to
    /// MpswpInfoBuildFlags alongside protocol.yaml) instead of silently dropping them.
    /// </summary>
    private static string DescribeBuildFlags(MpswpInfoBuildFlags flags)
    {
        if (flags == MpswpInfoBuildFlags.None)
        {
            return "Release";
        }
        var parts = new List<string>();
        if (flags.HasFlag(MpswpInfoBuildFlags.Debug))
        {
            parts.Add("DEBUG (nie używać w produkcji!)");
        }
        var unknown = flags & ~MpswpInfoBuildFlags.Debug;
        if (unknown != MpswpInfoBuildFlags.None)
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
            $"mpswp_{Client.NodeId:X2}_{DateTime.Now:yyyyMMdd_HHmmss}.csv");
        foreach (var channel in Enum.GetValues<MpswpChannel>())
        {
            var info = MpswpTables.Channels[channel];
            DashboardTiles.Add(new ChannelTileVm { Name = info.Label, Unit = info.Unit });
        }
    }

    private void OnTelemetry(object? sender, TelemetryRaw t)
    {
        OnTelemetryForSensorPoll();
        if (!Enum.IsDefined(typeof(MpswpChannel), t.Channel)) return;
        var channel = (MpswpChannel)t.Channel;
        var info = MpswpTables.Channels[channel];
        var idx = Array.IndexOf(Enum.GetValues<MpswpChannel>(), channel);
        if (idx < 0 || idx >= DashboardTiles.Count) return;
        var tile = DashboardTiles[idx];
        tile.IsValid = t.Valid;
        tile.RangeWarn = t.RangeWarn;
        tile.RangeErrDropped = t.RangeErrDropped;
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
            FileName = string.IsNullOrWhiteSpace(CsvPath) ? $"mpswp_{Client.NodeId:X2}_{DateTime.Now:yyyyMMdd_HHmmss}.csv" : Path.GetFileName(CsvPath),
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

    /// <summary>Side-panel groups: averaged channels first, then one group per sensor, one checkbox per quantity.</summary>
    public IReadOnlyList<ChartSeriesGroupVm> ChartSeriesGroups { get; }
    /// <summary>Curves of each plot, in legend order.</summary>
    public IReadOnlyList<ChartSeriesVm> TempSeries { get; }
    public IReadOnlyList<ChartSeriesVm> HumSeries { get; }
    public IReadOnlyList<ChartSeriesVm> PressSeries { get; }
    public IReadOnlyList<ChartSeriesVm> AirSeries { get; }
    private readonly Dictionary<string, ChartSeriesVm> _chartSeriesByKey = [];

    public ChartSeriesVm? FindChartSeries(string key) => _chartSeriesByKey.GetValueOrDefault(key);

    public IReadOnlyList<ChartSeriesVm> ChartSeriesFor(ChartTarget target) => target switch
    {
        ChartTarget.Temp => TempSeries,
        ChartTarget.Hum => HumSeries,
        ChartTarget.Press => PressSeries,
        ChartTarget.Air => AirSeries,
        _ => [],
    };

    public static string AverageSeriesKey(MpswpChannel channel) => $"avg_{channel}";
    public static string SensorSeriesKey(MpswpSensor sensor, MpswpQuantity quantity) => $"{sensor}_{quantity}";

    /// <summary>
    /// One color per SENSOR, identical on every plot: several sensors measure the same quantity (up to
    /// nine temperature curves), so a per-quantity color would not tell them apart, while a per-sensor
    /// color lets e.g. SHT45's temperature and humidity curves be matched at a glance. Tableau 10 order,
    /// readable on the white plot background; gray is left out as too close to the black averages.
    /// </summary>
    private static readonly IReadOnlyDictionary<MpswpSensor, string> SensorColors = new Dictionary<MpswpSensor, string>
    {
        [MpswpSensor.Htu21D] = "#1F77B4",
        [MpswpSensor.Sht45] = "#FF7F0E",
        [MpswpSensor.Mpl3115A2] = "#2CA02C",
        [MpswpSensor.Lps25Hb] = "#D62728",
        [MpswpSensor.Bme680] = "#9467BD",
        [MpswpSensor.Sts31Cpu] = "#8C564B",
        [MpswpSensor.Sts31Sens] = "#E377C2",
        [MpswpSensor.Tmp117] = "#BCBD22",
        [MpswpSensor.Mcu] = "#17BECF",
    };

    /// <summary>
    /// Averages are black — the fused curve is the reference the sensor curves are read against. The
    /// air-quality plot is the exception: it carries three averages and no sensor curves, so black for
    /// all three would make them indistinguishable.
    /// </summary>
    private static readonly IReadOnlyDictionary<MpswpChannel, string> AverageColors = new Dictionary<MpswpChannel, string>
    {
        [MpswpChannel.Temp] = "#000000",
        [MpswpChannel.Hum] = "#000000",
        [MpswpChannel.Press] = "#000000",
        [MpswpChannel.VocIndex] = "#1F77B4",
        [MpswpChannel.NoxIndex] = "#FF7F0E",
        [MpswpChannel.Iaq] = "#2CA02C",
    };

    private (IReadOnlyList<ChartSeriesGroupVm> Groups, IReadOnlyList<ChartSeriesVm> All) BuildChartSeries()
    {
        var groups = new List<ChartSeriesGroupVm>();

        // Averaged channels are shown by default: they arrive on every TELEMETRY_PERIOD without polling.
        var averages = new List<ChartSeriesVm>();
        foreach (var channel in Enum.GetValues<MpswpChannel>())
        {
            var target = AverageChartTargetFor(channel);
            if (target == ChartTarget.None) continue;
            var label = MpswpTables.Channels[channel].Label;
            averages.Add(new ChartSeriesVm
            {
                Key = AverageSeriesKey(channel), Label = label, LegendLabel = $"{label} (śr.)",
                Target = target, ColorHex = AverageColors[channel], IsAverage = true, IsChecked = true,
            });
        }
        groups.Add(new ChartSeriesGroupVm("Średnie (telemetria)", averages));

        foreach (var (sensor, quantities) in MpswpTables.SensorProvides)
        {
            var sensorName = MpswpTables.SensorNames[sensor];
            var series = new List<ChartSeriesVm>();
            foreach (var q in quantities)
            {
                var target = ChartTargetFor(q);
                if (target == ChartTarget.None) continue;
                // Each plot shows one quantity per sensor, and the axis title already names it, so the
                // legend needs only the sensor — its full name, since the two STS31 differ only there.
                series.Add(new ChartSeriesVm
                {
                    Key = SensorSeriesKey(sensor, q), Label = MpswpTables.Quantities[q].Label, LegendLabel = sensorName,
                    Target = target, ColorHex = SensorColors[sensor],
                });
            }
            if (series.Count > 0) groups.Add(new ChartSeriesGroupVm(sensorName, series));
        }

        var all = groups.SelectMany(g => g.Series).ToList();
        foreach (var s in all) _chartSeriesByKey[s.Key] = s;
        return (groups, all);
    }

    /// <summary>Plot of each averaged channel. IAQ has no per-sensor counterpart in <see cref="ChartTargetFor"/>
    /// (BME680 reports only raw gas resistance), yet its fused index belongs with VOC/NOx.</summary>
    private static ChartTarget AverageChartTargetFor(MpswpChannel c) => c switch
    {
        MpswpChannel.Temp => ChartTarget.Temp,
        MpswpChannel.Hum => ChartTarget.Hum,
        MpswpChannel.Press => ChartTarget.Press,
        MpswpChannel.VocIndex or MpswpChannel.NoxIndex or MpswpChannel.Iaq => ChartTarget.Air,
        _ => ChartTarget.None,
    };

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
        foreach (var s in Enum.GetValues<MpswpSensor>()) _ = Client.ReadSensorAsync(s);
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

    #region Params

    public ObservableCollection<ParamEditRowVm> Params { get; } = [];
    private readonly Dictionary<byte, ParamEditRowVm> _paramById = [];

    private void InitParams()
    {
        foreach (var p in MpswpParams.All)
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
        foreach (var p in MpswpParams.All)
        {
            await Client.ReadParamAsync(p.Id);
            await Task.Delay(10); // przerwa między ramkami — nie zalewamy magistrali (~90 parametrów)
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
        if (r.Opcode == (byte)MpswpReqOp.ReadParam && r.Status == StatusCode.Ok && r.Data.Length > 0 && _paramById.TryGetValue(r.Arg, out var row))
        {
            row.ApplyDeviceValue(ParamCodec.Decode(row.Descriptor.Type, r.Data));
        }
        else if (r.Opcode == (byte)MpswpReqOp.CalibAntenna && r.Status != StatusCode.Ok)
        {
            AntennaStatusText = $"Kalibracja nie ruszyła: {r.Status}";
        }
    }

    #endregion

    #region Sensors + antenna calibration

    public ObservableCollection<SensorRowVm> Sensors { get; } = [];
    private readonly Dictionary<byte, SensorRowVm> _sensorRows = [];
    [ObservableProperty] private string _antennaStatusText = "—";
    [ObservableProperty] private bool _isCalibratingAntenna;

    private void InitSensors()
    {
        foreach (var s in Enum.GetValues<MpswpSensor>())
        {
            var sensor = s;
            var row = new SensorRowVm(MpswpTables.SensorNames[s], sensor, () => Client.ReadSensorAsync(sensor));
            _sensorRows[(byte)s] = row;
            Sensors.Add(row);
        }
    }

    [RelayCommand]
    private async Task RefreshAllSensors()
    {
        await Client.ListSensorsAsync();
        foreach (var s in Enum.GetValues<MpswpSensor>())
        {
            await Client.ReadSensorAsync(s);
            await Task.Delay(15);
        }
    }

    [RelayCommand]
    private async Task CalibrateAntenna()
    {
        IsCalibratingAntenna = true;
        AntennaStatusText = "Kalibracja w toku (~1,2 s)...";
        await Client.CalibrateAntennaAsync();
    }

    private void OnAntennaCalibrated(object? sender, MpswpCalibResult r)
    {
        IsCalibratingAntenna = false;
        AntennaStatusText = r.Status == StatusCode.Ok
            ? $"OK — TUN_CAP={r.TunCap}, f_LCO={r.LcoHz / 1000.0:F1} kHz"
            : $"Błąd kalibracji: {r.Status}";
    }

    private void OnSensorsList(object? sender, IReadOnlyDictionary<byte, byte> map)
    {
        foreach (var (id, status) in map)
        {
            if (!_sensorRows.TryGetValue(id, out var row)) continue;
            row.Status = status switch { 0 => "obecny", 1 => "nieobecny", 2 => "awaria", 3 => "wyłączony", _ => $"0x{status:X2}" };
        }
    }

    private void OnSensorReadings(object? sender, (MpswpSensor Sensor, IReadOnlyList<MpswpSensorReading> Readings) e)
    {
        if (_sensorRows.TryGetValue((byte)e.Sensor, out var row))
            row.ApplyReadings(e.Readings);

        foreach (var reading in e.Readings)
        {
            if (reading.IsRejected) continue; // poza LTL/UTL — nie zniekształcaj wykresu odrzuconą wartością
            RaiseChartSample(SensorSeriesKey(e.Sensor, reading.Quantity), reading.Value);
        }
    }

    /// <summary>EVENT SENSOR_RANGE — updates the sensor row's warning/error indicator immediately, ahead of the next READ_SENSOR poll.</summary>
    private void OnSensorRangeChanged(object? sender, MpswpRangeEvent e)
    {
        if (_sensorRows.TryGetValue((byte)e.Sensor, out var row))
            row.ApplyRangeEvent(e.Quantity, e.Status);
    }

    /// <summary>
    /// Maps a per-sensor quantity onto whichever existing averaged-channel plot shares its physical unit.
    /// VOC_RAW/NOX_RAW are deliberately excluded (mapped to None): SGP41 only reports the raw counts per
    /// sensor, while the "Jakość powietrza" plot's average curve is the fused VOC_INDEX/NOX_INDEX — mixing
    /// raw (thousands) with index (1..500) on one axis makes both unreadable.
    /// </summary>
    private static ChartTarget ChartTargetFor(MpswpQuantity q) => q switch
    {
        MpswpQuantity.Temp or MpswpQuantity.McuTemp => ChartTarget.Temp,
        MpswpQuantity.Hum => ChartTarget.Hum,
        MpswpQuantity.Press => ChartTarget.Press,
        MpswpQuantity.VocIndex or MpswpQuantity.NoxIndex => ChartTarget.Air,
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
        DeviceErrFlags.Critical => "Błąd krytyczny aktywny (brak czujników lub awaria FPU)",
        DeviceErrFlags.SensorFault => "Co najmniej jeden czujnik w awarii",
        DeviceErrFlags.NoSensors => "Żaden czujnik nie dostarcza danych (stacja \"ślepa\")",
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

    private void OnTime(object? sender, MpswpRtcTime t) =>
        DeviceTimeText = $"{t.Year:D4}-{t.Month:D2}-{t.Day:D2} {t.Hour:D2}:{t.Minute:D2}:{t.Second:D2}";

    #endregion

    #region Lightning (AS3935)

    /// <summary>Raised on every decoded LIGHTNING event so the Charts view's code-behind can plot it (distance vs. time, energy encoded separately so it never distorts the distance axis).</summary>
    public event EventHandler<(int DistanceKm, int Energy)>? LightningStrikeReceived;

    #endregion

    #region Events

    public ObservableCollection<EventLogRowVm> EventLog { get; } = [];
    private const int MaxEventRows = 2000;

    private void OnEvent(object? sender, EventMessage e)
    {
        var code = Enum.IsDefined(typeof(MpswpEventCode), e.Code) ? ((MpswpEventCode)e.Code).ToString() : $"0x{e.Code:X2}";
        var source = e.Source == MpswpEventSource.System ? "SYSTEM"
            : Enum.IsDefined(typeof(MpswpSensor), e.Source) ? MpswpTables.SensorNames[(MpswpSensor)e.Source] : $"0x{e.Source:X2}";

        if (e.Code == (byte)MpswpEventCode.Lightning && e.Data.Length >= 4)
        {
            var distanceKm = e.Data[0];
            var energy = e.Data[1] | (e.Data[2] << 8) | (e.Data[3] << 16);
            LightningStrikeReceived?.Invoke(this, (distanceKm, energy));
        }

        AppendEventRow(source, code, e.Data, string.Join(' ', e.Data.Select(b => b.ToString("X2"))));
    }

    private void AppendEventRow(string source, string code, byte[] data, string dataHex)
    {
        EventLog.Insert(0, new EventLogRowVm(DateTimeOffset.Now, source, code, dataHex, DescribeEvent(code, data)));
        while (EventLog.Count > MaxEventRows) EventLog.RemoveAt(EventLog.Count - 1);
    }

    private void AppendEventRow(string source, string code, string dataHex) => AppendEventRow(source, code, [], dataHex);

    private static string DescribeEvent(string code, byte[] data) => code switch
    {
        nameof(MpswpEventCode.Boot) => "Start węzła",
        nameof(MpswpEventCode.Lightning) when data.Length >= 4 =>
            $"Wykryto wyładowanie — dystans {data[0]} km, energia {data[1] | (data[2] << 8) | (data[3] << 16)}",
        nameof(MpswpEventCode.Lightning) => "Wykryto wyładowanie",
        nameof(MpswpEventCode.SensorAlert) => "Alert progowy STS31",
        nameof(MpswpEventCode.SensorFault) => "Awaria czujnika",
        nameof(MpswpEventCode.SensorBack) => "Czujnik wrócił do sprawności",
        nameof(MpswpEventCode.BusRecovered) => "Odzysk magistrali / I2C",
        nameof(MpswpEventCode.Heartbeat) => "Diagnostyka cykliczna",
        nameof(MpswpEventCode.SensorIrq) => "Przerwanie PCF8574",
        nameof(MpswpEventCode.CriticalAlarm) => "BŁĄD KRYTYCZNY",
        nameof(MpswpEventCode.CalibDone) => "Zakończono autokalibrację anteny AS3935",
        nameof(MpswpEventCode.SensorRange) when data.Length >= 6 => DescribeSensorRange(data),
        nameof(MpswpEventCode.SensorRange) => "Zmiana statusu zakresu odczytu",
        _ => "",
    };

    private static string DescribeSensorRange(byte[] data)
    {
        var qtyByte = data[0];
        var status = (MpswpReadingStatus)data[1];
        var raw = BitConverter.ToInt32(data, 2);
        var qtyName = Enum.IsDefined(typeof(MpswpQuantity), qtyByte) ? MpswpNames.Of((MpswpQuantity)qtyByte) : $"0x{qtyByte:X2}";
        var valueText = Enum.IsDefined(typeof(MpswpQuantity), qtyByte) && MpswpTables.Quantities.TryGetValue((MpswpQuantity)qtyByte, out var qi)
            ? $"{raw * qi.Scale:F2} {qi.Unit}"
            : raw.ToString();
        return $"{qtyName}: {MpswpLimits.Describe(status)} ({valueText})";
    }

    #endregion

    public void Dispose()
    {
        _sensorPollTimer?.Dispose();
        Client.Dispose();
        _simulator?.Dispose();
    }
}
