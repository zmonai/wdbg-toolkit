using System.Text.Json;

namespace WdbgToolkit.Workflows;

/// <summary>
/// Manifest written alongside every workflow run's artifacts. This is the integration
/// contract for the future MCP server: it can scan <see cref="WorkflowRunStore.RootDirectory"/>
/// and read each run's manifest.json without any in-process coupling to this app.
/// </summary>
public sealed record WorkflowRunManifest(
    string ActionId,
    string ScenarioId,
    DateTimeOffset StartedAt,
    bool Succeeded,
    string Summary,
    IReadOnlyDictionary<string, string> Data,
    IReadOnlyList<string> Artifacts,
    string? Error);

/// <summary>
/// Creates the per-run artifact directory and writes <c>manifest.json</c> describing the
/// result of a workflow action run.
/// </summary>
public static class WorkflowRunStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };

    public static string RootDirectory { get; } =
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "WdbgToolkit");

    /// <summary>
    /// Creates <c>&lt;rootDirectory&gt;\&lt;scenarioId&gt;\&lt;timestamp&gt;\</c> (defaulting
    /// <paramref name="rootDirectory"/> to <see cref="RootDirectory"/>) and returns its
    /// path, ready for an action to write artifact files into.
    /// </summary>
    public static string CreateRunDirectory(string scenarioId, DateTimeOffset startedAt, string? rootDirectory = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scenarioId);

        var runDirectory = Path.Combine(
            rootDirectory ?? RootDirectory,
            scenarioId,
            startedAt.ToString("yyyyMMdd-HHmmss"));
        Directory.CreateDirectory(runDirectory);
        return runDirectory;
    }

    /// <summary>
    /// Writes <c>manifest.json</c> for a completed run into <paramref name="runDirectory"/>.
    /// </summary>
    public static string WriteManifest(
        string runDirectory,
        string actionId,
        string scenarioId,
        DateTimeOffset startedAt,
        WorkflowActionResult result)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runDirectory);

        var manifest = new WorkflowRunManifest(
            actionId,
            scenarioId,
            startedAt,
            result.Succeeded,
            result.Summary,
            result.Data,
            result.ArtifactPaths,
            result.Error);

        var manifestPath = Path.Combine(runDirectory, "manifest.json");
        File.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest, SerializerOptions));
        return manifestPath;
    }
}
