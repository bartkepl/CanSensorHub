using System.Windows;
using System.Windows.Controls;
using CanSensorHub.App.ViewModels;

namespace CanSensorHub.App;

/// <summary>
/// Picks a header/content DataTemplate by CLR type for the shell's single heterogeneous TabControl
/// (pinned <see cref="BusMonitorViewModel"/> tab + <see cref="DeviceEntry"/> device tabs). Needed because
/// plain implicit (DataType-keyed, unkeyed) DataTemplates in Resources would be picked for BOTH the tab
/// header AND the tab body for the same type — there's no way to give them different templates without
/// an explicit selector once more than one CLR type shares a TabControl.
/// </summary>
public sealed class TypeSwitchTemplateSelector : DataTemplateSelector
{
    public DataTemplate? BusMonitorTemplate { get; set; }
    public DataTemplate? DeviceTemplate { get; set; }

    public override DataTemplate? SelectTemplate(object? item, DependencyObject container) => item switch
    {
        BusMonitorViewModel => BusMonitorTemplate,
        DeviceEntry => DeviceTemplate,
        _ => base.SelectTemplate(item, container),
    };
}
