using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using CanSensorHub.Core.Can;
using CanSensorHub.Core.Tools;
using CanSensorHub.Metrology.Analysis;
using CanSensorHub.Metrology.Instruments;
using CanSensorHub.Metrology.Simulation;
using CanSensorHub.Metrology.Visa;
using CanSensorHub.Tools.MpccAfeCal.Calibration;
using CanSensorHub.Tools.MpccAfeCal.Node;
using CanSensorHub.Tools.MpccAfeCal.Reporting;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CanSensorHub.Tools.MpccAfeCal.ViewModels;

/// <summary>
/// Okno kalibracji AFE: wybór przyrządów i węzła, ustawienia, przebieg kalibracji, zapis do węzła
/// i sprawdzenie. Przyrządy i łącze z węzłem są otwierane na czas jednej operacji — między
/// operacjami narzędzie nie trzyma sesji VISA ani nie obciąża magistrali.
/// </summary>
public sealed partial class MpccAfeCalViewModel : ObservableObject, IDisposable
{
    private readonly HubToolContext _context;
    private VisaResourceManager? _visa;
    private string? _visaError;
    private CancellationTokenSource? _cts;
    private AfeCalibrationSession? _session;

    public AfeCalSettings Settings { get; }
    public bool IsSimulation => _context.Bus.Kind == ConnectionKind.Simulated;

    public ObservableCollection<VisaResourceItem> Resources { get; } = [];
    public ObservableCollection<HubToolDevice> Devices { get; } = [];
    public ObservableCollection<ChannelOptionVm> ChannelOptions { get; }
    public ObservableCollection<ChannelRowVm> Rows { get; }
    public ObservableCollection<PointRowVm> Points { get; } = [];
    public ObservableCollection<LogLineVm> Log { get; } = [];

    public IReadOnlyList<int> DacSlots => Daq34970A.Slots;
    public IReadOnlyList<int> DacOutputs { get; } = [1, 2];
    public IReadOnlyList<double> NplcOptions => Dmm34401ASettings.AllowedNplc;
    public IReadOnlyList<ModeOption> Modes { get; } =
    [
        new(CalibrationMode.TwoPoint, "Dwupunktowa (krańce zakresu)"),
        new(CalibrationMode.MultiPoint, "Wielopunktowa (najmniejsze kwadraty)"),
    ];

    [ObservableProperty] private VisaResourceItem? _selectedDmm;
    [ObservableProperty] private VisaResourceItem? _selectedDaq;
    [ObservableProperty] private bool _includeSerialPorts;
    [ObservableProperty] private string _instrumentStatus = "";
    [ObservableProperty] private HubToolDevice? _selectedDevice;
    [ObservableProperty] private string _nodeStatus = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CalibrateCommand), nameof(CheckCommand), nameof(WriteCommand), nameof(RefreshResourcesCommand),
        nameof(ReadNodeCommand), nameof(StopCommand))]
    private bool _isBusy;

    [ObservableProperty] private double _progress;
    [ObservableProperty] private string _statusText = "Gotowe.";
    [ObservableProperty] private string? _lastReportPath;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(WriteCommand))]
    private bool _hasPendingCalibration;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(WriteCommand))]
    private bool _writeConfirmed;

    /// <summary>Dane wykresu błędu się zmieniły; widok przerysowuje wykres.</summary>
    public event EventHandler<IReadOnlyList<ErrorSeries>>? ErrorSeriesChanged;

    public MpccAfeCalViewModel(HubToolContext context)
    {
        _context = context;
        Settings = AfeCalSettingsStore.Load();
        if (Settings.EnabledChannels.Length != AfeModel.ChannelCount)
            Settings.EnabledChannels = Enumerable.Repeat(true, AfeModel.ChannelCount).ToArray();
        ChannelOptions = new(AfeModel.Channels.Select(c => new ChannelOptionVm(c, Settings.EnabledChannels[c.Index])));
        Rows = new(AfeModel.Channels.Select(c => new ChannelRowVm(c.Index)));

        if (!IsSimulation)
        {
            _visa = VisaResourceManager.TryCreate(out _visaError);
            InstrumentStatus = _visaError ?? "Odśwież listę, aby wyszukać przyrządy.";
        }
        else
        {
            InstrumentStatus = "Tryb symulatora: zadajnik i multimetr symulowane, podłączone do wejść symulowanych węzłów.";
        }
        RefreshDevices();
    }

    // --- przyrządy -------------------------------------------------------------------------------

    private bool CanUseVisa() => !IsBusy && _visa is not null;

    [RelayCommand(CanExecute = nameof(CanUseVisa))]
    private async Task RefreshResources()
    {
        IsBusy = true;
        InstrumentStatus = "Wyszukiwanie przyrządów…";
        try
        {
            var visa = _visa!;
            var includeSerial = IncludeSerialPorts;
            var found = await Task.Run(() =>
            {
                var items = new List<VisaResourceItem>();
                foreach (var address in visa.FindInstruments(includeSerial))
                {
                    try
                    {
                        using var s = visa.Open(address, TimeSpan.FromSeconds(2));
                        items.Add(new VisaResourceItem(address, ScpiCommon.IdentifyAsync(s).GetAwaiter().GetResult(), null));
                    }
                    catch (Exception ex)
                    {
                        items.Add(new VisaResourceItem(address, null, ex.Message));
                    }
                }
                return items;
            });

            Resources.Clear();
            foreach (var r in found) Resources.Add(r);
            SelectedDmm = Pick(Settings.DmmResource, InstrumentKind.Dmm34401A);
            SelectedDaq = Pick(Settings.DaqResource, InstrumentKind.Daq34970A);
            InstrumentStatus = found.Count == 0
                ? "Nie znaleziono przyrządów GPIB/USB/LAN. Sprawdzić zasilanie przyrządów i adapter."
                : $"Znaleziono {found.Count}: {string.Join(", ", found.Select(f => f.Identity?.Model ?? "?"))}.";
        }
        catch (Exception ex)
        {
            InstrumentStatus = $"Błąd wyszukiwania: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Zapamiętany adres, a gdy go brak — pierwszy przyrząd rozpoznany jako oczekiwany model.</summary>
    private VisaResourceItem? Pick(string? remembered, InstrumentKind kind) =>
        Resources.FirstOrDefault(r => r.Address == remembered)
        ?? Resources.FirstOrDefault(r => r.Identity?.Kind == kind);

    private sealed class Bench(IVoltageSource source, IVoltmeter voltmeter, string sourceId, string referenceId, IDisposable? owner) : IDisposable
    {
        public IVoltageSource Source => source;
        public IVoltmeter Voltmeter => voltmeter;
        public string SourceId => sourceId;
        public string ReferenceId => referenceId;
        public void Dispose() => owner?.Dispose();
    }

    private sealed class Sessions(params IDisposable[] items) : IDisposable
    {
        public void Dispose() { foreach (var i in items) i.Dispose(); }
    }

    private async Task<Bench> OpenBenchAsync(CancellationToken ct)
    {
        if (IsSimulation)
        {
            var node = new AnalogInputNode(_context.Bus.SimulatedBus!.AnalogInput);
            return new Bench(new SimulatedVoltageSource(node, Settings.MinVoltage, Settings.MaxVoltage), new SimulatedVoltmeter(node),
                "zadajnik symulowany", "woltomierz symulowany", null);
        }

        if (_visa is null) throw new InvalidOperationException(_visaError ?? "VISA niedostępna.");
        if (SelectedDmm is null || SelectedDaq is null) throw new InvalidOperationException("Wybierz multimetr 34401A i jednostkę 34970A.");
        if (SelectedDmm.Address == SelectedDaq.Address) throw new InvalidOperationException("Multimetr i zadajnik muszą być różnymi przyrządami.");

        var dmmSession = _visa.Open(SelectedDmm.Address, TimeSpan.FromSeconds(5));
        var daqSession = _visa.Open(SelectedDaq.Address, TimeSpan.FromSeconds(5));
        try
        {
            var dmmId = await ScpiCommon.IdentifyAsync(dmmSession, ct);
            var daqId = await ScpiCommon.IdentifyAsync(daqSession, ct);
            if (dmmId.Kind != InstrumentKind.Dmm34401A) throw new InvalidOperationException($"{SelectedDmm.Address} to {dmmId.Model}, nie 34401A.");
            if (daqId.Kind != InstrumentKind.Daq34970A) throw new InvalidOperationException($"{SelectedDaq.Address} to {daqId.Model}, nie 34970A.");

            var daq = new Daq34970A(daqSession);
            var cards = await daq.GetCardModelsAsync(ct);
            if (cards[Settings.DacSlot] != "34907A")
            {
                var found = cards.Where(c => c.Value == "34907A").Select(c => c.Key.ToString()).ToList();
                throw new InvalidOperationException($"W gnieździe {Settings.DacSlot} nie ma karty 34907A" +
                    (found.Count > 0 ? $" (karta 34907A w gnieździe {string.Join(", ", found)})." : "."));
            }

            var source = new Dac34907AChannel(daq, Settings.DacSlot, Settings.DacOutput, Settings.MinVoltage, Settings.MaxVoltage);
            var dmm = new Dmm34401A(dmmSession, new Dmm34401ASettings { Nplc = Settings.DmmNplc, AutoZero = Settings.DmmAutoZero, RangeVolts = 10 });
            Settings.DmmResource = SelectedDmm.Address;
            Settings.DaqResource = SelectedDaq.Address;
            return new Bench(source, dmm, $"{daqId} / {source.Description}", dmmId.ToString(), new Sessions(dmmSession, daqSession));
        }
        catch
        {
            dmmSession.Dispose();
            daqSession.Dispose();
            throw;
        }
    }

    // --- węzeł -----------------------------------------------------------------------------------

    [RelayCommand]
    private void RefreshDevices()
    {
        var current = SelectedDevice;
        Devices.Clear();
        foreach (var d in _context.GetDevices().Where(d => d.ModuleId == "MPCC")) Devices.Add(d);
        SelectedDevice = Devices.FirstOrDefault(d => d == current) ?? Devices.FirstOrDefault();
        if (Devices.Count == 0) NodeStatus = "Brak dodanych urządzeń MPCC — dodaj urządzenie w oknie głównym.";
    }

    private bool CanRun() => !IsBusy && SelectedDevice is not null && _context.Bus.IsConnected;

    partial void OnSelectedDeviceChanged(HubToolDevice? value)
    {
        CalibrateCommand.NotifyCanExecuteChanged();
        CheckCommand.NotifyCanExecuteChanged();
        ReadNodeCommand.NotifyCanExecuteChanged();
    }

    private MpccNodeLink OpenLink() => new(_context.Bus, SelectedDevice!.NodeId);

    [RelayCommand(CanExecute = nameof(CanRun))]
    private Task ReadNode() => RunAsync("Odczyt węzła", async (link, _) =>
    {
        await ReadNodeStateAsync(link);
        return true;
    }, needsBench: false, keepSession: true);

    private async Task<AfeNodeState> ReadNodeStateAsync(MpccNodeLink link, CancellationToken ct = default)
    {
        var info = await link.GetInfoAsync(ct);
        var state = await AfeNodeState.ReadAsync(link, ct);
        NodeStatus = $"NODE 0x{link.NodeId:X2} · UID {info.UidHex ?? "—"} · FW {info.FwMajor}.{info.FwMinor} build {info.BuildRevision} · " +
                     $"MEASURE_PERIOD {state.MeasurePeriodMs:0} ms · Tref {state.Tref:0.0} °C · CAL_LOCK {(state.CalLocked ? "założona" : "ZDJĘTA")}";
        foreach (var row in Rows)
        {
            var c = state.Coefficients[row.Channel];
            (row.C0Before, row.C1Before, row.Tc) = (c.C0, c.C1, c.Tc);
        }
        if (_session is not null) _session.Node = info;
        return state;
    }

    // --- operacje --------------------------------------------------------------------------------

    [RelayCommand(CanExecute = nameof(CanRun))]
    private Task Calibrate() => RunAsync("Kalibracja", async (link, bench) =>
    {
        HasPendingCalibration = false;
        WriteConfirmed = false;
        Points.Clear();
        foreach (var r in Rows) { r.ClearCalibration(); r.ClearCheck(); }

        var state = await ReadNodeStateAsync(link, _cts!.Token);
        _session!.CoefficientsBefore = state.Coefficients;
        (_session.Tref, _session.MeasurePeriodMs) = (state.Tref, state.MeasurePeriodMs);

        var count = Settings.Mode == CalibrationMode.TwoPoint ? 2 : Settings.CalibrationPoints;
        var setpoints = Setpoints.Linear(Settings.MinVoltage, Settings.MaxVoltage, count);
        _session.CalibrationPoints = await MeasureAsync(link, bench!, state, setpoints, "kalibracja");
        PublishSeries();

        _session.Calibrations = AfeCalibrationCalculator.Compute(_session.CalibrationPoints, state.Coefficients, state.Tref, Settings);
        foreach (var c in _session.Calibrations)
        {
            var row = Rows[c.Channel];
            row.Gain = c.Fit.Slope;
            row.OffsetMv = c.Fit.Intercept * 1000.0;
            row.MaxResidualMv = c.Fit.MaxAbsResidual * 1000.0;
            row.C0After = c.After.C0;
            row.C1After = c.After.C1;
            row.CalibrationStatus = c.Rejection ?? "do zapisu";
            row.CalibrationRejected = !c.Accepted;
        }
        foreach (var ch in Settings.EnabledChannelIndices.Where(ch => _session.Calibrations.All(c => c.Channel != ch)))
            Rows[ch].CalibrationStatus = "za mało punktów poza nasyceniem ADC";

        HasPendingCalibration = _session.Calibrations.Any(c => c.Accepted);
        AddLog(ProcedureMessageKind.Info, HasPendingCalibration
            ? "Współczynniki wyznaczone. Sprawdź zestawienie, zatwierdź i zapisz do węzła."
            : "Żaden kanał nie ma przyjętych współczynników — zapis niemożliwy.");
        return true;
    });

    private bool CanWrite() => CanRun() && HasPendingCalibration && WriteConfirmed;

    [RelayCommand(CanExecute = nameof(CanWrite))]
    private Task Write() => RunAsync("Zapis i sprawdzenie", async (link, bench) =>
    {
        var session = _session!;
        var outcome = await new MpccCalibrationWriter(link).WriteAsync(session.Calibrations, _cts!.Token);
        session.Write = outcome;
        foreach (var line in outcome.Log) AddLog(outcome.Success ? ProcedureMessageKind.Info : ProcedureMessageKind.Warning, line);
        HasPendingCalibration = false;
        WriteConfirmed = false;
        if (!outcome.Success)
        {
            AddLog(ProcedureMessageKind.Error, "Zapis nie powiódł się — węzeł pozostał przy poprzednich współczynnikach.");
            return false;
        }
        foreach (var c in session.Calibrations.Where(c => c.Accepted)) Rows[c.Channel].CalibrationStatus = "zapisano";
        await RunCheckAsync(link, bench!, session);
        return true;
    }, keepSession: true);

    [RelayCommand(CanExecute = nameof(CanRun))]
    private Task Check() => RunAsync("Sprawdzenie", async (link, bench) =>
    {
        HasPendingCalibration = false;
        WriteConfirmed = false;
        Points.Clear();
        foreach (var r in Rows) { r.ClearCalibration(); r.ClearCheck(); }
        await RunCheckAsync(link, bench!, _session!);
        return true;
    });

    private async Task RunCheckAsync(MpccNodeLink link, Bench bench, AfeCalibrationSession session)
    {
        var state = await ReadNodeStateAsync(link, _cts!.Token);
        session.CheckCoefficients = state.Coefficients;
        if (session.CoefficientsBefore.Count == 0)
        {
            session.CoefficientsBefore = state.Coefficients;
            (session.Tref, session.MeasurePeriodMs) = (state.Tref, state.MeasurePeriodMs);
        }

        var setpoints = Setpoints.Interleaved(Settings.MinVoltage, Settings.MaxVoltage, Settings.CheckPoints);
        session.CheckPoints = await MeasureAsync(link, bench, state, setpoints, "sprawdzenie");
        session.Checks = AfeCalibrationCalculator.Evaluate(session.CheckPoints, state.Coefficients, Settings);
        PublishSeries();

        foreach (var c in session.Checks)
        {
            var row = Rows[c.Channel];
            row.CheckMaxErrorMv = c.Points.Count > 0 ? c.MaxAbsErrorMv : null;
            row.CheckPass = c.Points.Count > 0 ? c.Pass : null;
            row.CheckVerdict = c.Points.Count == 0 ? "brak danych"
                : (c.Pass ? "PASS" : "FAIL") + (c.SaturatedPoints > 0 ? $" ({c.SaturatedPoints} pkt w nasyceniu)" : "");
        }
        var failed = session.Checks.Where(c => !c.Pass).Select(c => $"CH{c.Channel}").ToList();
        AddLog(failed.Count == 0 ? ProcedureMessageKind.Info : ProcedureMessageKind.Warning,
            failed.Count == 0 ? $"Sprawdzenie: wszystkie kanały w tolerancji ±{Settings.ToleranceMv:0.###} mV."
                              : $"Sprawdzenie: poza tolerancją ±{Settings.ToleranceMv:0.###} mV — {string.Join(", ", failed)}.");
    }

    private async Task<IReadOnlyList<PointMeasurement>> MeasureAsync(
        MpccNodeLink link, Bench bench, AfeNodeState state, IReadOnlyList<double> setpoints, string phase)
    {
        var (round, settle) = AfeCalibrationProcedure.Timing(Settings, state.MeasurePeriodMs);
        var progress = new Progress<ProcedureProgress>(p =>
        {
            Progress = p.Fraction;
            if (p.Message is { } m)
            {
                StatusText = m.Text;
                if (m.Kind != ProcedureMessageKind.Info || p.Point is not null) AddLog(m.Kind, m.Text);
            }
            if (p.Point is { } pt) Points.Add(PointRowVm.From(phase, pt));
        });
        AddLog(ProcedureMessageKind.Info,
            $"{char.ToUpper(phase[0])}{phase[1..]}: {setpoints.Count} pkt ({string.Join("; ", setpoints.Select(s => s.ToString("0.000")))} V), " +
            $"ustalanie {settle.TotalSeconds:0.0} s, {Settings.NodeRounds} rund co {round.TotalSeconds:0.0} s.");
        var procedure = new AfeCalibrationProcedure(bench.Source, bench.Voltmeter, link, Settings, round, settle);
        return await procedure.MeasureAsync(setpoints, progress, _cts!.Token);
    }

    /// <summary>
    /// Wspólna otoczka operacji: walidacja, przyrządy, łącze z węzłem, anulowanie, raport.
    /// <paramref name="keepSession"/> — operacja kontynuuje sesję poprzedniej (zapis po kalibracji).
    /// </summary>
    private async Task RunAsync(string title, Func<MpccNodeLink, Bench?, Task<bool>> body, bool needsBench = true, bool keepSession = false)
    {
        SyncChannelOptions();
        if (Settings.Validate() is { } invalid)
        {
            AddLog(ProcedureMessageKind.Error, invalid);
            StatusText = invalid;
            return;
        }
        AfeCalSettingsStore.Save(Settings);

        IsBusy = true;
        Progress = 0;
        StatusText = $"{title}…";
        _cts = new CancellationTokenSource();
        if (!keepSession || _session is null)
            _session = new AfeCalibrationSession { NodeId = SelectedDevice!.NodeId, Settings = Settings.Clone(), Simulated = IsSimulation };

        Bench? bench = null;
        using var link = OpenLink();
        try
        {
            if (needsBench)
            {
                bench = await OpenBenchAsync(_cts.Token);
                (_session.SourceInstrument, _session.ReferenceInstrument) = (bench.SourceId, bench.ReferenceId);
                AddLog(ProcedureMessageKind.Info, $"Zadajnik: {bench.SourceId}");
                AddLog(ProcedureMessageKind.Info, $"Wzorzec: {bench.ReferenceId}");
            }
            var ok = await body(link, bench);
            StatusText = ok ? $"{title}: zakończono." : $"{title}: zakończono z błędem.";
        }
        catch (OperationCanceledException)
        {
            StatusText = $"{title}: przerwano.";
            AddLog(ProcedureMessageKind.Warning, $"{title} przerwane przez operatora. Zadajnik ustawiony na 0 V.");
        }
        catch (Exception ex)
        {
            StatusText = $"{title}: błąd.";
            AddLog(ProcedureMessageKind.Error, ex.Message);
        }
        finally
        {
            bench?.Dispose();
            _cts.Dispose();
            _cts = null;
            IsBusy = false;
            Progress = 0;
            if (needsBench) SaveReport();
        }
    }

    [RelayCommand(CanExecute = nameof(IsBusy))]
    private void Stop() => _cts?.Cancel();

    private void SaveReport()
    {
        if (_session is null || (_session.CalibrationPoints.Count == 0 && _session.CheckPoints.Count == 0)) return;
        try
        {
            // Nazwa pliku wynika z sesji (UID węzła, chwila rozpoczęcia), więc kolejne operacje tej
            // samej sesji — kalibracja, potem zapis i sprawdzenie — uzupełniają jeden raport.
            var path = Path.Combine(AfeCalSettingsStore.ReportDirectory, AfeCalibrationReport.DefaultFileName(_session));
            AfeCalibrationReport.Write(path, _session);
            LastReportPath = path;
            AddLog(ProcedureMessageKind.Info, $"Raport: {path}");
        }
        catch (Exception ex)
        {
            AddLog(ProcedureMessageKind.Error, $"Nie zapisano raportu: {ex.Message}");
        }
    }

    [RelayCommand]
    private void OpenReportFolder()
    {
        Directory.CreateDirectory(AfeCalSettingsStore.ReportDirectory);
        Process.Start(new ProcessStartInfo { FileName = AfeCalSettingsStore.ReportDirectory, UseShellExecute = true });
    }

    private void SyncChannelOptions() =>
        Settings.EnabledChannels = ChannelOptions.OrderBy(c => c.Index).Select(c => c.Enabled).ToArray();

    private void AddLog(ProcedureMessageKind kind, string text)
    {
        Log.Add(new LogLineVm(DateTime.Now, kind, text));
        while (Log.Count > 2000) Log.RemoveAt(0);
    }

    private void PublishSeries()
    {
        var series = new List<ErrorSeries>();
        if (_session is null) return;
        void Add(IReadOnlyList<PointMeasurement> pts, bool after)
        {
            foreach (var ch in Settings.EnabledChannelIndices)
            {
                var data = pts.Where(p => p.Channels[ch] is not null)
                    .Select(p => (p.Reference.Mean, (p.Channels[ch]!.Value.Mean - p.Reference.Mean) * 1000.0)).ToList();
                if (data.Count > 0) series.Add(new ErrorSeries(ch, after, data));
            }
        }
        Add(_session.CalibrationPoints, after: false);
        Add(_session.CheckPoints, after: true);
        ErrorSeriesChanged?.Invoke(this, series);
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _visa?.Dispose();
        _visa = null;
        SyncChannelOptions();
        AfeCalSettingsStore.Save(Settings);
    }

    /// <summary>Wejście analogowe magistrali symulowanej jako punkt połączeniowy przyrządów symulowanych.</summary>
    private sealed class AnalogInputNode(SimulatedAnalogInput input) : IAnalogNode
    {
        public double? Voltage { get => input.Voltage; set => input.Voltage = value; }
    }
}
