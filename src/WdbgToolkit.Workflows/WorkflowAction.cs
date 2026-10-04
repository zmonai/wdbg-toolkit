namespace WdbgToolkit.Workflows;

/// <summary>
/// Describes a single optional input to a <see cref="IWorkflowAction"/>.
/// </summary>
public sealed record WorkflowActionParameter(
    string Name,
    string Description,
    bool Required,
    string? DefaultValue = null);

/// <summary>
/// Static metadata describing a workflow action. This is the shape surfaced to the
/// WPF UI today and, later, to the MCP server's tool registration — both read from the
/// same <see cref="WorkflowActionCatalog"/>.
/// </summary>
public sealed record WorkflowActionDefinition(
    string Id,
    string ScenarioId,
    string Name,
    string Description,
    IReadOnlyList<WorkflowActionParameter> Parameters);

/// <summary>
/// Outcome of running a workflow action. <see cref="Data"/> and <see cref="ArtifactPaths"/>
/// are the structured hand-off an MCP tool call would return to an LLM.
/// </summary>
public sealed record WorkflowActionResult(
    bool Succeeded,
    string Summary,
    IReadOnlyDictionary<string, string> Data,
    IReadOnlyList<string> ArtifactPaths,
    string? Error = null)
{
    public static WorkflowActionResult Failure(string summary, string error) =>
        new(false, summary, new Dictionary<string, string>(), [], error);
}

/// <summary>
/// A single, reusable diagnostic action. Implementations must not depend on any UI
/// framework so they can be invoked identically from the desktop app and from the
/// future MCP server.
/// </summary>
public interface IWorkflowAction
{
    WorkflowActionDefinition Definition { get; }

    Task<WorkflowActionResult> ExecuteAsync(
        IReadOnlyDictionary<string, string> parameters,
        CancellationToken cancellationToken = default);
}
