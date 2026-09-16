using CanSensorHub.Core.Modules;

namespace CanSensorHub.App.ViewModels;

/// <summary>Pairs a live device instance with the module descriptor that created it (needed to persist/recreate it across sessions).</summary>
public sealed record DeviceEntry(IDeviceModuleDescriptor Descriptor, IDeviceModuleInstance Instance);
