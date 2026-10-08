using WdbgToolkit.PackageManagement;

namespace WdbgToolkit.Workflows.Actions;

/// <summary>
/// Starts a Windows Performance Recorder (WPR) trace. <c>wpr -start</c> returns as soon as
/// the trace session begins; the actual ETW capture continues in the background until
/// <see cref="StopTraceAction"/> is run.
/// </summary>
public sealed class StartTraceAction(ICommandRunner commandRunner) : IWorkflowAction
{
    public const string DefaultProfile = "GeneralProfile.Light";

    public WorkflowActionDefinition Definition { get; } = new(
        "performance.start-trace",
        "performance",
        "Start performance trace",
        "Start a Windows Performance Recorder (WPR) trace using the given profile (defaults to GeneralProfile.Light).",
        [new WorkflowActionParameter("profile", "WPR profile name.", Required: false, DefaultValue: DefaultProfile)]);

    public async Task<WorkflowActionResult> ExecuteAsync(
        IReadOnlyDictionary<string, string> parameters,
        CancellationToken cancellationToken = default)
    {
        var profile = parameters.TryGetValue("profile", out var requested) && !string.IsNullOrWhiteSpace(requested)
            ? requested
            : DefaultProfile;

        var result = await commandRunner.RunAsync("wpr", ["-start", profile], cancellationToken)
            .ConfigureAwait(false);

        return new WorkflowActionResult(
            result.ExitCode == 0,
            result.ExitCode == 0
                ? $"Trace started with profile '{profile}'."
                : $"wpr -start exited with code {result.ExitCode}.",
            new Dictionary<string, string> { ["profile"] = profile },
            [],
            result.ExitCode == 0 ? null : result.StandardError.Trim());
    }
}

/// <summary>
/// Stops the running WPR trace started by <see cref="StartTraceAction"/> and saves it as
/// an .etl artifact.
/// </summary>
public sealed class StopTraceAction(ICommandRunner commandRunner) : IWorkflowAction
{
    public const string DefaultDescription = "WdbgToolkit performance trace";

    public WorkflowActionDefinition Definition { get; } = new(
        "performance.stop-trace",
        "performance",
        "Stop performance trace",
        "Stop the running WPR trace and save it to an .etl file.",
        [new WorkflowActionParameter(
            "description",
            "Short description embedded in the trace file.",
            Required: false,
            DefaultValue: DefaultDescription)]);

    public async Task<WorkflowActionResult> ExecuteAsync(
        IReadOnlyDictionary<string, string> parameters,
        CancellationToken cancellationToken = default)
    {
        var description = parameters.TryGetValue("description", out var requested) &&
                           !string.IsNullOrWhiteSpace(requested)
            ? requested
            : DefaultDescription;
        var runDirectory = parameters.TryGetValue("RunDirectory", out var directory) && !string.IsNullOrWhiteSpace(directory)
            ? directory
            : Path.GetTempPath();
        var tracePath = Path.Combine(runDirectory, "trace.etl");

        var result = await commandRunner.RunAsync("wpr", ["-stop", tracePath, description], cancellationToken)
            .ConfigureAwait(false);

        var succeeded = result.ExitCode == 0 && File.Exists(tracePath);
        return new WorkflowActionResult(
            succeeded,
            succeeded
                ? $"Trace saved to {tracePath}."
                : $"wpr -stop exited with code {result.ExitCode}.",
            new Dictionary<string, string> { ["file"] = tracePath },
            succeeded ? [tracePath] : [],
            succeeded ? null : result.StandardError.Trim());
    }
}
