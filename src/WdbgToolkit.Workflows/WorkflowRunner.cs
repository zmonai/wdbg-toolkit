namespace WdbgToolkit.Workflows;

/// <summary>
/// Runs a workflow action end-to-end: creates its run directory, invokes it with
/// <c>RunDirectory</c> injected into its parameters, and writes the resulting
/// <c>manifest.json</c>. Both the WPF UI and the future MCP server should drive actions
/// through this runner so every run produces the same on-disk artifact contract.
/// </summary>
public sealed class WorkflowRunner(WorkflowActionCatalog catalog, string? runStoreRootDirectory = null)
{
    public async Task<WorkflowActionResult> RunAsync(
        string actionId,
        IReadOnlyDictionary<string, string>? parameters = null,
        CancellationToken cancellationToken = default)
    {
        var action = catalog.GetAction(actionId);
        var startedAt = DateTimeOffset.UtcNow;
        var runDirectory = WorkflowRunStore.CreateRunDirectory(
            action.Definition.ScenarioId,
            startedAt,
            runStoreRootDirectory);

        var effectiveParameters = new Dictionary<string, string>(
            parameters ?? new Dictionary<string, string>(),
            StringComparer.OrdinalIgnoreCase)
        {
            ["RunDirectory"] = runDirectory
        };

        var result = await action.ExecuteAsync(effectiveParameters, cancellationToken).ConfigureAwait(false);

        WorkflowRunStore.WriteManifest(
            runDirectory,
            action.Definition.Id,
            action.Definition.ScenarioId,
            startedAt,
            result);

        return result;
    }
}
