using CanSensorHub.Core.Can;
using CanSensorHub.Core.Modules;
using CanSensorHub.Modules.Mpswp.Protocol;
using CanSensorHub.Modules.Mpswp.ViewModels;

namespace CanSensorHub.Modules.Mpswp;

/// <summary>Catalog entry the shell's "Add device" dialog uses to instantiate MPSWP device panels.</summary>
public sealed class MpswpModuleDescriptor : IDeviceModuleDescriptor
{
    public string ModuleId => "MPSWP";
    public string DisplayName => "MPSWP — WeatherStationCan (polowy sensor warunków pogodowych)";
    public string ShortName => "MPSWP";
    public byte DefaultNodeId => MpswpNodes.Default;
    public string Summary => "STM32L432 · 9× czujników I2C, fuzja ważona · AS3935 wyładowania · RTC";
    public bool SupportsSimulation => true;
    public ushort DeviceType => MpswpInfo.DeviceType;

    public byte BootloaderNodeId => MpswpNodes.Default;
    public byte EnterBootloaderOpcode => (byte)MpswpReqOp.EnterBootloader;

    public IDeviceModuleInstance CreateInstance(DeviceModuleContext context)
    {
        IDisposable? sim = null;
        if (context.Bus.Kind == ConnectionKind.Simulated && context.Bus.SimulatedBus is { } simBus)
            sim = new MpswpSimulator(simBus, context.NodeId);
        return new MpswpDeviceViewModel(context, sim);
    }

    public IDisposable StartSimulator(SimulatedCanBus bus, byte nodeId) => new MpswpSimulator(bus, nodeId);
}
