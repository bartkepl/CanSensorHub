using System.Reflection;
using CanSensorHub.Core.Can;

namespace CanSensorHub.Core.Tools;

/// <summary>Urządzenie dodane w powłoce, widziane przez narzędzie: typ modułu i adres węzła.</summary>
public sealed record HubToolDevice(string ModuleId, byte NodeId, string InstanceName);

/// <summary>
/// Kontekst przekazywany narzędziu przy otwarciu. Magistrala jest współdzielona z modułami
/// urządzeń; narzędzie nie otwiera własnego połączenia CAN.
/// </summary>
public sealed class HubToolContext
{
    public required CanBusService Bus { get; init; }

    /// <summary>Bieżąca lista dodanych urządzeń. Wywoływana przy każdym odświeżeniu, bo lista zmienia się w czasie pracy narzędzia.</summary>
    public required Func<IReadOnlyList<HubToolDevice>> GetDevices { get; init; }

    /// <summary>Okno powłoki jako właściciel okna narzędzia. Typ <c>object</c>, bo rdzeń nie zna WPF.</summary>
    public object? Owner { get; init; }
}

/// <summary>
/// Narzędzie serwisowe otwierane z menu „Narzędzia” powłoki — np. kalibracja jednej wielkości
/// jednego typu urządzenia. Narzędzie jest osobnym projektem, wykrywanym w czasie działania
/// (<see cref="HubToolDiscovery"/>), więc może zostać wyłączone z buildu bez zmian w powłoce.
/// </summary>
public interface IHubTool
{
    /// <summary>Stabilny klucz maszynowy, np. <c>"MPCC.AFE_CAL"</c>.</summary>
    string ToolId { get; }
    string DisplayName { get; }
    string Description { get; }

    /// <summary><see cref="Modules.IDeviceModuleDescriptor.ModuleId"/> urządzenia, którego dotyczy narzędzie.</summary>
    string TargetModuleId { get; }

    /// <summary>Otwiera okno narzędzia albo aktywuje już otwarte.</summary>
    void Open(HubToolContext context);
}

/// <summary>Oznacza zestaw jako dostarczający narzędzie. Jeden atrybut na narzędzie.</summary>
[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
public sealed class HubToolAttribute(Type toolType) : Attribute
{
    public Type ToolType { get; } = toolType;
}

/// <summary>
/// Wykrywanie narzędzi w katalogu aplikacji. Zestawy narzędzi trafiają tam przez warunkową
/// referencję projektową powłoki (właściwość MSBuild <c>WithTools</c>); ich brak oznacza po
/// prostu pustą listę.
/// </summary>
public static class HubToolDiscovery
{
    public const string AssemblyPattern = "CanSensorHub.Tools.*.dll";

    public static IReadOnlyList<IHubTool> DiscoverInDirectory(string directory, Action<string>? onError = null)
    {
        var assemblies = new List<Assembly>();
        foreach (var path in Directory.EnumerateFiles(directory, AssemblyPattern).OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                // Ładowanie po nazwie, nie po ścieżce: zestaw jest referencją powłoki, więc
                // domyślny kontekst i tak by go wczytał. LoadFrom utworzyłby drugą kopię typów.
                assemblies.Add(Assembly.Load(AssemblyName.GetAssemblyName(path)));
            }
            catch (Exception ex)
            {
                onError?.Invoke($"{Path.GetFileName(path)}: {ex.Message}");
            }
        }
        return DiscoverInAssemblies(assemblies, onError);
    }

    public static IReadOnlyList<IHubTool> DiscoverInAssemblies(IEnumerable<Assembly> assemblies, Action<string>? onError = null)
    {
        var tools = new List<IHubTool>();
        foreach (var asm in assemblies)
        {
            foreach (var attr in asm.GetCustomAttributes<HubToolAttribute>())
            {
                try
                {
                    if (!typeof(IHubTool).IsAssignableFrom(attr.ToolType))
                        throw new InvalidOperationException($"{attr.ToolType.FullName} nie implementuje {nameof(IHubTool)}.");
                    tools.Add((IHubTool)Activator.CreateInstance(attr.ToolType)!);
                }
                catch (Exception ex)
                {
                    onError?.Invoke($"{asm.GetName().Name}: {ex.Message}");
                }
            }
        }
        return tools
            .GroupBy(t => t.ToolId, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderBy(t => t.DisplayName, StringComparer.CurrentCulture)
            .ToList();
    }
}
