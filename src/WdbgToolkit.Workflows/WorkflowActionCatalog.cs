using WdbgToolkit.PackageManagement;
using WdbgToolkit.Workflows.Actions;

namespace WdbgToolkit.Workflows;

/// <summary>
/// Registers every <see cref="IWorkflowAction"/> available in the toolkit. This is the
/// single source both the WPF UI and a future MCP server's tool registration read from,
/// so action behavior never has to be duplicated between the two.
/// </summary>
public sealed class WorkflowActionCatalog
{
    private readonly IReadOnlyList<IWorkflowAction> _actions;

    public WorkflowActionCatalog(
        ICommandRunner commandRunner,
        string? crashDumpDirectory = null,
        string? customScriptsDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(commandRunner);

        _actions =
        [
            new ListCrashDumpsAction(crashDumpDirectory),
            new AnalyzeLatestDumpAction(commandRunner, crashDumpDirectory),
            new StartTraceAction(commandRunner),
            new StopTraceAction(commandRunner),
            new CapturePacketsAction(commandRunner),
            new SummarizeCaptureAction(commandRunner),
            new ListCustomScriptsAction(customScriptsDirectory),
            new RunCustomScriptAction(commandRunner, customScriptsDirectory)
        ];
    }

    public IReadOnlyList<WorkflowActionDefinition> All =>
        Array.AsReadOnly(_actions.Select(action => action.Definition).ToArray());

    public IReadOnlyList<WorkflowActionDefinition> GetByScenario(string scenarioId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scenarioId);

        return Array.AsReadOnly(
            _actions
                .Select(action => action.Definition)
                .Where(definition => string.Equals(definition.ScenarioId, scenarioId, StringComparison.OrdinalIgnoreCase))
                .ToArray());
    }

    public IWorkflowAction GetAction(string actionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actionId);

        return _actions.FirstOrDefault(
                   action => string.Equals(action.Definition.Id, actionId, StringComparison.OrdinalIgnoreCase))
               ?? throw new KeyNotFoundException($"No workflow action is registered with id '{actionId}'.");
    }
}
