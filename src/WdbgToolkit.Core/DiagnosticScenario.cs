namespace WdbgToolkit.Core;

public enum ScenarioAvailability
{
    Planned,
    Available
}

public sealed record DiagnosticScenario(
    string Id,
    string Name,
    string Description,
    IReadOnlyList<string> Tools,
    IReadOnlyList<string> PrerequisiteToolIds,
    string PrerequisiteDescription,
    ScenarioAvailability Availability);

public static class ScenarioCatalog
{
    private static readonly IReadOnlyList<DiagnosticScenario> Scenarios =
        Array.AsReadOnly<DiagnosticScenario>(
        [
            new(
                "crash",
                "Crash analysis",
                "Organize crash dumps and prepare a repeatable WinDbg analysis session.",
                ["WinDbg"],
                ["windbg", "sysinternals-suite", "procdump"],
                "Install WinDbg and Sysinternals Suite, then register ProcDump as the postmortem debugger with full-memory dumps enabled.",
                ScenarioAvailability.Planned),
            new(
                "performance",
                "Performance",
                "Capture and review Windows performance traces for slowdowns and hangs.",
                ["Windows Performance Toolkit (WPR / WPA)"],
                ["windows-performance-toolkit", "sysinternals-suite"],
                "Install Windows Performance Toolkit and Sysinternals Suite for trace capture and supporting diagnostics.",
                ScenarioAvailability.Planned),
            new(
                "networking",
                "Networking",
                "Collect packet captures and supporting network diagnostics.",
                ["Wireshark", "Npcap"],
                ["wireshark", "npcap", "sysinternals-suite"],
                "Install Wireshark, Npcap, and Sysinternals Suite for packet capture and network troubleshooting. Npcap is installed through Chocolatey.",
                ScenarioAvailability.Planned),
            new(
                "mcp-server",
                "MCP Server",
                "Run the wdbgmcp server so an MCP-compatible LLM client can read workflow run data collected by this toolkit.",
                ["wdbgmcp (Node.js MCP server)"],
                ["nodejs"],
                "Install Node.js, required to run the wdbgmcp server.",
                ScenarioAvailability.Available)
        ]);

    public static IReadOnlyList<DiagnosticScenario> All => Scenarios;

    public static IReadOnlyList<string> AllPrerequisiteToolIds =>
        Array.AsReadOnly(
            Scenarios
                .SelectMany(scenario => scenario.PrerequisiteToolIds)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray());
}
