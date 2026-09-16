using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CanSensorHub.Core.Bootloader;
using CanSensorHub.Core.Can;
using CanSensorHub.Core.Modules;
using CanSensorHub.Core.Protocol;
using Microsoft.Win32;

namespace CanSensorHub.App.ViewModels;

/// <summary>
/// Shell-level firmware flashing tool, shared by every module instead of duplicated per-device: the
/// CAN bootloader protocol (HELLO/ERASE/PROG/VERIFY/GO) is byte-for-byte identical between
/// MPCC_BL and MPSWP_BL (a deliberate 1:1 port — see each firmware's PROTOCOL.md), so one implementation
/// parameterized by the selected module's <see cref="IDeviceModuleDescriptor.BootloaderNodeId"/> and
/// <see cref="IDeviceModuleDescriptor.EnterBootloaderOpcode"/> covers both. Mirrors the reference Python
/// apps' Bootloader tab: one combined "Programuj" sequence (ENTER → wait → HELLO → [ERASE] → program →
/// VERIFY → [GO]) gated by checkboxes, plus a running log — rather than separate buttons per step.
/// </summary>
public partial class BootloaderToolViewModel : ObservableObject
{
    private readonly CanBusService _bus;

    public IReadOnlyList<IDeviceModuleDescriptor> Modules { get; }
    public ObservableCollection<string> LogLines { get; } = [];

    [ObservableProperty] private IDeviceModuleDescriptor? _selectedModule;
    [ObservableProperty] private string _appNodeIdText = "";
    [ObservableProperty] private string? _firmwareFilePath;
    [ObservableProperty] private double _flashProgress;
    [ObservableProperty] private bool _isFlashing;
    [ObservableProperty] private bool _enterBootloader = true;
    [ObservableProperty] private bool _eraseBeforeProgram = true;
    [ObservableProperty] private bool _goAfterProgram = true;

    public BootloaderToolViewModel(CanBusService bus, ModuleRegistry registry)
    {
        _bus = bus;
        Modules = registry.Modules;
        SelectedModule = Modules.FirstOrDefault();
        Log("Gotowy.");
    }

    partial void OnSelectedModuleChanged(IDeviceModuleDescriptor? value)
    {
        if (value is not null) AppNodeIdText = $"0x{value.DefaultNodeId:X2}";
    }

    [RelayCommand]
    private void BrowseFirmwareFile()
    {
        var dlg = new OpenFileDialog { Filter = "Obrazy firmware (*.bin;*.hex)|*.bin;*.hex|Wszystkie pliki (*.*)|*.*" };
        if (dlg.ShowDialog() == true) FirmwareFilePath = dlg.FileName;
    }

    [RelayCommand]
    private async Task ProgramViaCan()
    {
        if (SelectedModule is null) { Log("Wybierz typ modułu."); return; }
        if (!_bus.IsConnected) { Log("Brak połączenia CAN — połącz się w oknie głównym (pasek u góry)."); return; }
        if (!ValidateFirmwareFile()) return;
        if (EnterBootloader && !TryParseNodeId(out _)) return;

        await RunProgramSequenceAsync(() => new BlCanLink(_bus, SelectedModule.BootloaderNodeId), sendEnter: EnterBootloader);
    }

    private async Task RunProgramSequenceAsync(Func<IBlLink> linkFactory, bool sendEnter)
    {
        IsFlashing = true;
        FlashProgress = 0;
        LogLines.Clear();
        try
        {
            var image = FirmwareImage.LoadFromFile(FirmwareFilePath!);

            if (sendEnter && SelectedModule is not null && TryParseNodeId(out var appNodeId))
            {
                Log("ENTER_BOOTLOADER -> aplikacja...");
                var frame = EnvelopeCodec.BuildRequest(appNodeId, SelectedModule.EnterBootloaderOpcode, 0,
                    [CommandGuard.EnterBootloader]);
                await _bus.SendAsync(frame);
                await Task.Delay(300); // daj węzłowi czas na odłożony reset i start bootloadera
            }

            using var link = linkFactory();
            var flasher = new BootloaderFlasher(link);

            Log("oczekiwanie na bootloader...");
            var hello = await flasher.WaitForBootloaderAsync(TimeSpan.FromSeconds(6), CancellationToken.None);
            Log($"BL v{hello.VersionText}, app_valid={hello.AppValid}, block={hello.BlockSize} B");

            // Typ urządzenia zgłaszany jest dopiero od wersji 1.1 bootloadera. Wersja 1.0 pracuje
            // w module zalanym i takiej informacji nie podaje — kontrola jest wtedy pomijana,
            // a odpowiedzialność za wybór obrazu spoczywa na operatorze.
            if (hello.DeviceType is { } reportedType && SelectedModule is not null)
            {
                Log($"typ urządzenia zgłoszony przez bootloader: 0x{reportedType:X4}");
                if (reportedType != SelectedModule.DeviceType)
                {
                    Log($"PRZERWANO: obraz przeznaczony dla 0x{SelectedModule.DeviceType:X4}, " +
                        $"a węzeł zgłasza 0x{reportedType:X4}. Pamięć nie została naruszona.");
                    return;
                }
            }
            else if (hello.VersionMinor == 0)
            {
                Log("bootloader 1.0 nie zgłasza typu urządzenia — sprawdź samodzielnie, " +
                    "czy obraz odpowiada węzłowi.");
            }

            Log($"obraz {image.Data.Length} B, CRC32 0x{image.Crc32Value:X8}");

            if (EraseBeforeProgram)
            {
                Log("kasowanie slotu aplikacji...");
                await flasher.EraseAsync(CancellationToken.None);
            }

            var startedAt = DateTime.UtcNow;
            var progress = new Progress<BlFlashProgress>(p => FlashProgress = p.Fraction * 100.0);
            await flasher.ProgramAsync(image, hello.BlockSize, progress, CancellationToken.None);
            var seconds = Math.Max((DateTime.UtcNow - startedAt).TotalSeconds, 0.001);
            Log($"zapisano {image.Data.Length} B w {seconds:F1}s ({image.Data.Length / seconds / 1024:F1} KB/s)");

            Log("weryfikacja CRC...");
            await flasher.VerifyAsync((uint)image.Data.Length, image.Crc32Value, CancellationToken.None);
            Log("VERIFY OK");

            if (GoAfterProgram)
            {
                Log("skok do aplikacji (GO)...");
                await flasher.GoAsync(CancellationToken.None);
            }

            Log("OK: aplikacja wgrana i zweryfikowana.");
        }
        catch (Exception ex)
        {
            Log($"BŁĄD: {ex.Message}");
        }
        finally
        {
            IsFlashing = false;
        }
    }

    private bool ValidateFirmwareFile()
    {
        if (!string.IsNullOrWhiteSpace(FirmwareFilePath) && File.Exists(FirmwareFilePath)) return true;
        MessageBox.Show("Wybierz plik obrazu firmware (.bin lub .hex).", "Bootloader", MessageBoxButton.OK, MessageBoxImage.Warning);
        return false;
    }

    private bool TryParseNodeId(out byte nodeId)
    {
        var text = AppNodeIdText.Trim();
        var ok = text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? byte.TryParse(text.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out nodeId)
            : byte.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out nodeId);
        if (!ok) Log("Nieprawidłowy adres węzła aplikacji (NODE_ID).");
        return ok;
    }

    private void Log(string message) => LogLines.Add(message);
}
