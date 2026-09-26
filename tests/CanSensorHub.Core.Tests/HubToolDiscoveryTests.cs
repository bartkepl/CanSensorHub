using System.Reflection;
using CanSensorHub.Core.Tools;
using CanSensorHub.Core.Tests;

[assembly: HubTool(typeof(FakeHubToolB))]
[assembly: HubTool(typeof(FakeHubToolA))]
[assembly: HubTool(typeof(FakeHubToolDuplicate))]
[assembly: HubTool(typeof(NotATool))]

namespace CanSensorHub.Core.Tests;

public sealed class FakeHubToolA : IHubTool
{
    public string ToolId => "TEST.A";
    public string DisplayName => "A narzędzie";
    public string Description => "";
    public string TargetModuleId => "MPCC";
    public void Open(HubToolContext context) { }
}

public sealed class FakeHubToolB : IHubTool
{
    public string ToolId => "TEST.B";
    public string DisplayName => "B narzędzie";
    public string Description => "";
    public string TargetModuleId => "MPSWP";
    public void Open(HubToolContext context) { }
}

public sealed class FakeHubToolDuplicate : IHubTool
{
    public string ToolId => "test.a";
    public string DisplayName => "Z duplikat";
    public string Description => "";
    public string TargetModuleId => "MPCC";
    public void Open(HubToolContext context) { }
}

public sealed class NotATool;

public class HubToolDiscoveryTests
{
    [Fact]
    public void Discovers_tools_declared_by_assembly_attributes_sorted_by_name()
    {
        var errors = new List<string>();
        var tools = HubToolDiscovery.DiscoverInAssemblies([Assembly.GetExecutingAssembly()], errors.Add);

        Assert.Equal(["TEST.A", "TEST.B"], tools.Select(t => t.ToolId));
        Assert.IsType<FakeHubToolA>(tools[0]);
    }

    [Fact]
    public void Type_not_implementing_the_contract_is_reported_not_thrown()
    {
        var errors = new List<string>();
        HubToolDiscovery.DiscoverInAssemblies([Assembly.GetExecutingAssembly()], errors.Add);

        Assert.Single(errors);
        Assert.Contains(nameof(NotATool), errors[0]);
    }

    [Fact]
    public void Directory_without_tool_assemblies_yields_empty_list()
    {
        var dir = Directory.CreateTempSubdirectory("hubtools-").FullName;
        try
        {
            Assert.Empty(HubToolDiscovery.DiscoverInDirectory(dir));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
