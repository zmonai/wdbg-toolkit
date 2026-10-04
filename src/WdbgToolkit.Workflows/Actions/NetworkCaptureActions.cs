using WdbgToolkit.PackageManagement;

namespace WdbgToolkit.Workflows.Actions;

/// <summary>
/// Captures network traffic for a bounded duration using dumpcap and saves it as a
/// .pcapng file. A bounded, synchronous capture (via dumpcap's <c>-a duration:</c>
/// autostop condition) is used instead of separate start/stop actions so the action
/// fits the same request/response execution model as every other workflow action.
/// </summary>
public sealed class CapturePacketsAction(ICommandRunner commandRunner) : IWorkflowAction
{
    public const string DefaultInterface = "1";
    public const int DefaultDurationSeconds = 30;

    public WorkflowActionDefinition Definition { get; } = new(
        "networking.capture-packets",
        "networking",
        "Capture network packets",
        "Capture network traffic for a bounded duration using dumpcap and save it as a .pcapng file.",
        [
            new WorkflowActionParameter(
                "interface",
                "Network interface index or name to capture on (dumpcap -i value). Defaults to 1 (first interface).",
                Required: false,
                DefaultValue: DefaultInterface),
            new WorkflowActionParameter(
                "durationSeconds",
                "How many seconds to capture before automatically stopping.",
                Required: false,
                DefaultValue: DefaultDurationSeconds.ToString())
        ]);

    public async Task<WorkflowActionResult> ExecuteAsync(
        IReadOnlyDictionary<string, string> parameters,
        CancellationToken cancellationToken = default)
    {
        var networkInterface = parameters.TryGetValue("interface", out var requestedInterface) &&
                                !string.IsNullOrWhiteSpace(requestedInterface)
            ? requestedInterface
            : DefaultInterface;
        var durationSeconds = parameters.TryGetValue("durationSeconds", out var requestedDuration) &&
                               int.TryParse(requestedDuration, out var parsedDuration) &&
                               parsedDuration > 0
            ? parsedDuration
            : DefaultDurationSeconds;
        var runDirectory = parameters.TryGetValue("RunDirectory", out var directory) && !string.IsNullOrWhiteSpace(directory)
            ? directory
            : Path.GetTempPath();
        var capturePath = Path.Combine(runDirectory, "capture.pcapng");

        var result = await commandRunner.RunAsync(
                "dumpcap",
                ["-i", networkInterface, "-a", $"duration:{durationSeconds}", "-w", capturePath],
                cancellationToken)
            .ConfigureAwait(false);

        var succeeded = result.ExitCode == 0 && File.Exists(capturePath);
        return new WorkflowActionResult(
            succeeded,
            succeeded
                ? $"Captured {durationSeconds}s of traffic to {capturePath}."
                : $"dumpcap exited with code {result.ExitCode}.",
            new Dictionary<string, string>
            {
                ["interface"] = networkInterface,
                ["durationSeconds"] = durationSeconds.ToString(),
                ["file"] = capturePath
            },
            succeeded ? [capturePath] : [],
            succeeded ? null : result.StandardError.Trim());
    }
}

/// <summary>
/// Runs <c>tshark -r &lt;file&gt; -q -z io,phs</c> against a capture file and captures the
/// protocol-hierarchy summary as a text artifact.
/// </summary>
public sealed class SummarizeCaptureAction(ICommandRunner commandRunner) : IWorkflowAction
{
    public WorkflowActionDefinition Definition { get; } = new(
        "networking.summarize-capture",
        "networking",
        "Summarize packet capture",
        "Run tshark -r <file> -q -z io,phs against a .pcapng file and capture the protocol-hierarchy summary.",
        [new WorkflowActionParameter("capturePath", "Path to a .pcapng file to summarize.", Required: true)]);

    public async Task<WorkflowActionResult> ExecuteAsync(
        IReadOnlyDictionary<string, string> parameters,
        CancellationToken cancellationToken = default)
    {
        if (!parameters.TryGetValue("capturePath", out var capturePath) || string.IsNullOrWhiteSpace(capturePath))
        {
            return WorkflowActionResult.Failure(
                "No capture file was provided.",
                "The 'capturePath' parameter is required.");
        }

        var result = await commandRunner.RunAsync(
                "tshark",
                ["-r", capturePath, "-q", "-z", "io,phs"],
                cancellationToken)
            .ConfigureAwait(false);

        var artifacts = new List<string>();
        if (parameters.TryGetValue("RunDirectory", out var runDirectory) && !string.IsNullOrWhiteSpace(runDirectory))
        {
            var summaryPath = Path.Combine(runDirectory, "summary.txt");
            await File.WriteAllTextAsync(summaryPath, result.StandardOutput, cancellationToken)
                .ConfigureAwait(false);
            artifacts.Add(summaryPath);
        }

        return new WorkflowActionResult(
            result.ExitCode == 0,
            result.ExitCode == 0
                ? $"Summarized {Path.GetFileName(capturePath)}."
                : $"tshark exited with code {result.ExitCode}.",
            new Dictionary<string, string> { ["capturePath"] = capturePath },
            artifacts,
            result.ExitCode == 0 ? null : result.StandardError.Trim());
    }
}
