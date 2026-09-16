using CanSensorHub.Core.Can;
using CanSensorHub.Core.Modules;
using CanSensorHub.Modules.Mpcc.Protocol;
using CanSensorHub.Modules.Mpcc.ViewModels;

namespace CanSensorHub.Modules.Mpcc;

/// <summary>Catalog entry the shell's "Add device" dialog uses to instantiate MPCC device panels.</summary>
public sealed class MpccModuleDescriptor : IDeviceModuleDescriptor
{
    public string ModuleId => "MPCC";
    public string DisplayName => "MPCC — MIL_PSU_CAN (monitoring pakietu bateryjnego)";
    public string ShortName => "MPCC";
    public byte DefaultNodeId => MpccNodes.Default;
    public string Summary => "STM32L432 · 7× ADC + STS31 + RTC · wyjście open-drain · OLED";
    public bool SupportsSimulation => true;
    public ushort DeviceType => 0x0002;

    public byte BootloaderNodeId => MpccNodes.Default;
    public byte EnterBootloaderOpcode => (byte)MpccReqOp.EnterBootloader;

    public IDeviceModuleInstance CreateInstance(DeviceModuleContext context)
    {
        IDisposable? sim = null;
        if (context.Bus.Kind == ConnectionKind.Simulated && context.Bus.SimulatedBus is { } simBus)
            sim = new MpccSimulator(simBus, context.NodeId);
        return new MpccDeviceViewModel(context, sim);
    }

    public IDisposable StartSimulator(SimulatedCanBus bus, byte nodeId) => new MpccSimulator(bus, nodeId);
}
