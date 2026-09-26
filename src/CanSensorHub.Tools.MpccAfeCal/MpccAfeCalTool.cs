using System.Windows;
using CanSensorHub.Core.Tools;
using CanSensorHub.Tools.MpccAfeCal;
using CanSensorHub.Tools.MpccAfeCal.ViewModels;
using CanSensorHub.Tools.MpccAfeCal.Views;

[assembly: HubTool(typeof(MpccAfeCalTool))]
[assembly: ThemeInfo(ResourceDictionaryLocation.None, ResourceDictionaryLocation.SourceAssembly)]

namespace CanSensorHub.Tools.MpccAfeCal;

/// <summary>Wpis narzędzia w menu „Narzędzia” powłoki. Jedno okno naraz — ponowne otwarcie aktywuje istniejące.</summary>
public sealed class MpccAfeCalTool : IHubTool
{
    private MpccAfeCalWindow? _window;

    public string ToolId => "MPCC.AFE_CAL";
    public string DisplayName => "Kalibracja wejść napięciowych MPCC…";
    public string Description => "Kalibracja c0/c1 siedmiu wejść AFE węzła MPCC zadajnikiem 34907A i multimetrem 34401A, ze sprawdzeniem wielopunktowym.";
    public string TargetModuleId => "MPCC";

    public void Open(HubToolContext context)
    {
        if (_window is not null)
        {
            if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
            _window.Activate();
            return;
        }
        _window = new MpccAfeCalWindow(new MpccAfeCalViewModel(context)) { Owner = context.Owner as Window };
        _window.Closed += (_, _) => _window = null;
        _window.Show();
    }
}
