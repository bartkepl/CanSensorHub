using CanSensorHub.Core.Can;

namespace CanSensorHub.Core.Modules;

/// <summary>
/// Context handed to a module when the user adds a device instance in the shell: the shared bus
/// connection, the CAN node address to talk to, and a user-editable label for the tab/instance list.
/// </summary>
public sealed class DeviceModuleContext
{
    public required CanBusService Bus { get; init; }
    public required byte NodeId { get; init; }
    public required string InstanceName { get; init; }
}

/// <summary>
/// One live device instance the user added to the shell (a module type bound to a specific NODE id).
/// Kept UI-framework agnostic in Core: <see cref="View"/> is the module's root WPF UserControl, typed as
/// <c>object</c> so this project never needs to reference WPF. The App/module projects (which do
/// reference WPF) cast it back to <c>UIElement</c> when hosting it in a tab.
/// </summary>
public interface IDeviceModuleInstance : IDisposable
{
    byte NodeId { get; }
    string InstanceName { get; }
    object View { get; }
    string HeaderText { get; }
    bool IsOnline { get; }
    event EventHandler? HeaderChanged;
}

/// <summary>Describes an installable device module type (MPCC, MPSWP, ... future GeigerProbe) for the "Add device" catalog.</summary>
public interface IDeviceModuleDescriptor
{
    /// <summary>Stable machine key, e.g. "MPCC".</summary>
    string ModuleId { get; }
    string DisplayName { get; }
    string ShortName { get; }
    byte DefaultNodeId { get; }
    /// <summary>One-line hardware summary shown in the "Add device" dialog.</summary>
    string Summary { get; }

    IDeviceModuleInstance CreateInstance(DeviceModuleContext context);

    /// <summary>True if this module can spin up an in-process fake node for the Symulator connection mode.</summary>
    bool SupportsSimulation { get; }

    /// <summary>Attaches a fake device endpoint to <paramref name="bus"/> responding as NODE <paramref name="nodeId"/>. Returns a handle to stop it.</summary>
    IDisposable StartSimulator(SimulatedCanBus bus, byte nodeId);

    /// <summary>
    /// Fixed CAN node address the module's bootloader (MPCC_BL/MPSWP_BL-style) listens/responds on —
    /// compiled into the bootloader image, independent of whatever NODE_ID the application firmware was
    /// reconfigured to. Used by the shell's shared Bootloader tool.
    /// </summary>
    byte BootloaderNodeId { get; }

    /// <summary>
    /// DEVICE_TYPE z rejestru typów urządzeń warstwy wspólnej. Węzeł zgłasza go w ramce
    /// identyfikacyjnej (profil 2) oraz w odpowiedzi HELLO bootloadera (wersja 1.1), co pozwala
    /// potwierdzić, że pod danym adresem pracuje urządzenie tego rodzaju, zanim host zinterpretuje
    /// jego telemetrię albo wgra obraz firmware.
    /// </summary>
    ushort DeviceType { get; }

    /// <summary>
    /// REQUEST opcode (OBJ field) the *running application* firmware uses to jump into its bootloader.
    /// Sent to the app's current NODE_ID (which the Bootloader tool asks for separately, since it may
    /// differ from <see cref="DefaultNodeId"/>), with the shared guard byte 0xB0 as the single data byte.
    /// </summary>
    byte EnterBootloaderOpcode { get; }
}

/// <summary>Catalog of module types the shell offers in "Add device". Populated once at startup.</summary>
public sealed class ModuleRegistry
{
    private readonly List<IDeviceModuleDescriptor> _modules = [];

    public IReadOnlyList<IDeviceModuleDescriptor> Modules => _modules;

    public void Register(IDeviceModuleDescriptor descriptor) => _modules.Add(descriptor);

    public IDeviceModuleDescriptor? Find(string moduleId) =>
        _modules.FirstOrDefault(m => string.Equals(m.ModuleId, moduleId, StringComparison.OrdinalIgnoreCase));
}
