using WdbgToolkit.Core;

namespace WdbgToolkit.Core.Tests;

public sealed class ScenarioCatalogTests
{
    [Fact]
    public void AllScenariosHaveUniqueNonEmptyIds()
    {
        var scenarios = ScenarioCatalog.All;

        Assert.NotEmpty(scenarios);
        Assert.All(scenarios, scenario => Assert.False(string.IsNullOrWhiteSpace(scenario.Id)));
        Assert.Equal(
            scenarios.Count,
            scenarios.Select(scenario => scenario.Id).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void CatalogCoversInitialDiagnosticAreas()
    {
        var scenarioIds = ScenarioCatalog.All.Select(scenario => scenario.Id);

        Assert.Contains("crash", scenarioIds);
        Assert.Contains("performance", scenarioIds);
        Assert.Contains("networking", scenarioIds);
        Assert.Contains("mcp-server", scenarioIds);
        Assert.DoesNotContain("custom-logs", scenarioIds);
    }

    [Fact]
    public void InitialDiagnosticScenariosAreNotAdvertisedAsAvailable()
    {
        Assert.All(
            ScenarioCatalog.All.Where(scenario => scenario.Id != "mcp-server"),
            scenario => Assert.Equal(ScenarioAvailability.Planned, scenario.Availability));
    }

    [Fact]
    public void McpServerScenarioIsAvailable()
    {
        var scenario = ScenarioCatalog.All.Single(item => item.Id == "mcp-server");

        Assert.Equal(ScenarioAvailability.Available, scenario.Availability);
        Assert.Equal(["nodejs"], scenario.PrerequisiteToolIds);
    }

    [Fact]
    public void EachDiagnosticScenarioHasItsOwnPrerequisiteSet()
    {
        Assert.Equal(
            ["windbg", "sysinternals-suite", "procdump"],
            ScenarioCatalog.All.Single(scenario => scenario.Id == "crash").PrerequisiteToolIds);
        Assert.Equal(
            ["windows-performance-toolkit", "sysinternals-suite"],
            ScenarioCatalog.All.Single(scenario => scenario.Id == "performance").PrerequisiteToolIds);
        Assert.Equal(
            ["wireshark", "npcap", "sysinternals-suite"],
            ScenarioCatalog.All.Single(scenario => scenario.Id == "networking").PrerequisiteToolIds);
        Assert.Contains("Npcap", ScenarioCatalog.All.Single(scenario => scenario.Id == "networking").Tools);
    }

    [Fact]
    public void AllPrerequisiteToolIdsCombinesDefinedScenarioRequirementsWithoutDuplicates()
    {
        Assert.Equal(
            [
                "windbg",
                "sysinternals-suite",
                "procdump",
                "windows-performance-toolkit",
                "wireshark",
                "npcap",
                "nodejs"
            ],
            ScenarioCatalog.AllPrerequisiteToolIds);
    }
}
