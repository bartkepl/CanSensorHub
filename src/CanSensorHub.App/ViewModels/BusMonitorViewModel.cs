using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CanSensorHub.Core.Can;
using Microsoft.Win32;

namespace CanSensorHub.App.ViewModels;

/// <summary>
/// The shell's global raw CAN bus monitor: every frame sent or received on the shared connection,
/// envelope-decoded (FUNC/NODE/OBJ/ARG) but module-agnostic — this is the "communication log" the main
/// application owns, independent of whichever device modules are currently attached.
/// </summary>
public partial class BusMonitorViewModel : ObservableObject
{
    private const int MaxRows = 5000;
    private readonly CanBusService _bus;

    public ObservableCollection<CanLogEntry> Entries { get; } = [];
    public ICollectionView EntriesView { get; }

    [ObservableProperty] private bool _isPaused;
    [ObservableProperty] private string _filterText = "";
    [ObservableProperty] private int _totalCount;

    public BusMonitorViewModel(CanBusService bus)
    {
        _bus = bus;
        EntriesView = CollectionViewSource.GetDefaultView(Entries);
        EntriesView.Filter = FilterPredicate;
        _bus.FrameLogged += OnFrameLogged;
    }

    private void OnFrameLogged(object? sender, CanLogEntry entry)
    {
        if (IsPaused) return;
        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            Entries.Insert(0, entry);
            while (Entries.Count > MaxRows) Entries.RemoveAt(Entries.Count - 1);
            TotalCount++;
        });
    }

    partial void OnFilterTextChanged(string value) => EntriesView.Refresh();

    private bool FilterPredicate(object obj)
    {
        if (string.IsNullOrWhiteSpace(FilterText)) return true;
        if (obj is not CanLogEntry e) return false;
        var needle = FilterText.Trim();
        return e.FuncName.Contains(needle, StringComparison.OrdinalIgnoreCase)
            || e.Id.Node.ToString("X2").Contains(needle, StringComparison.OrdinalIgnoreCase)
            || e.Id.Obj.ToString("X2").Contains(needle, StringComparison.OrdinalIgnoreCase)
            || e.DataHex.Contains(needle, StringComparison.OrdinalIgnoreCase)
            || e.Frame.Id.ToString("X8").Contains(needle, StringComparison.OrdinalIgnoreCase);
    }

    [RelayCommand]
    private void Clear()
    {
        Entries.Clear();
        TotalCount = 0;
    }

    [RelayCommand]
    private void TogglePause() => IsPaused = !IsPaused;

    [RelayCommand]
    private void Export()
    {
        var dlg = new SaveFileDialog { Filter = "Pliki CSV (*.csv)|*.csv", FileName = $"can_log_{DateTime.Now:yyyyMMdd_HHmmss}.csv" };
        if (dlg.ShowDialog() != true) return;

        using var writer = new StreamWriter(dlg.FileName, false, System.Text.Encoding.UTF8);
        writer.WriteLine("czas,kierunek,id_hex,func,node,obj,arg,dlc,dane_hex");
        foreach (var e in Entries.Reverse())
        {
            writer.WriteLine(string.Join(',',
                e.Timestamp.ToString("O"), e.Direction, $"0x{e.Frame.Id:X8}", e.FuncName,
                $"0x{e.Id.Node:X2}", $"0x{e.Id.Obj:X2}", $"0x{e.Id.Arg:X2}", e.Frame.Data.Length, e.DataHex));
        }
    }
}
