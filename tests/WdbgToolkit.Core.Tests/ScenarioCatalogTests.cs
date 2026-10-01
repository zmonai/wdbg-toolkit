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
        Assert.Contains("custom-logs", scenarioIds);
    }

    [Fact]
    public void InitialScenariosAreNotAdvertisedAsAvailable()
    {
        Assert.All(
            ScenarioCatalog.All,
            scenario => Assert.Equal(ScenarioAvailability.Planned, scenario.Availability));
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
    public void CustomLogsHasPlaceholderInsteadOfInstallablePrerequisites()
    {
        var scenario = ScenarioCatalog.All.Single(item => item.Id == "custom-logs");

        Assert.Empty(scenario.PrerequisiteToolIds);
        Assert.Contains("not been defined", scenario.PrerequisiteDescription);
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
                "npcap"
            ],
            ScenarioCatalog.AllPrerequisiteToolIds);
    }
}
