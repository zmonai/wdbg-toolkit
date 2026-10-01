using WdbgToolkit.PackageManagement;

namespace WdbgToolkit.Workflows.Actions;

/// <summary>
/// Lists the crash dumps captured by ProcDump's postmortem debugger registration
/// (see <see cref="PostmortemDebuggerConfigurator"/>).
/// </summary>
public sealed class ListCrashDumpsAction(string? dumpDirectory = null) : IWorkflowAction
{
    public WorkflowActionDefinition Definition { get; } = new(
        "crash.list-dumps",
        "crash",
        "List crash dumps",
        "List .dmp files captured by ProcDump's postmortem debugger.",
        []);

    private string DumpDirectory => dumpDirectory ?? PostmortemDebuggerConfigurator.DefaultDumpDirectory;

    public Task<WorkflowActionResult> ExecuteAsync(
        IReadOnlyDictionary<string, string> parameters,
        CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(DumpDirectory))
        {
            return Task.FromResult(new WorkflowActionResult(
                true,
                "No dump directory found yet. Install the crash prerequisites first.",
                new Dictionary<string, string> { ["count"] = "0" },
                []));
        }

        var dumps = Directory.GetFiles(DumpDirectory, "*.dmp")
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .ToArray();

        var data = new Dictionary<string, string>
        {
            ["count"] = dumps.Length.ToString(),
            ["latest"] = dumps.Length > 0 ? Path.GetFileName(dumps[0]) : string.Empty
        };

        return Task.FromResult(new WorkflowActionResult(
            true,
            dumps.Length == 0
                ? "No crash dumps found."
                : $"Found {dumps.Length} crash dump(s). Latest: {data["latest"]}.",
            data,
            dumps));
    }
}

/// <summary>
/// Runs <c>cdb -z &lt;dump&gt; -c "!analyze -v;q"</c> against a crash dump and captures the
/// analysis output as a text artifact.
/// </summary>
public sealed class AnalyzeLatestDumpAction(ICommandRunner commandRunner, string? dumpDirectory = null)
    : IWorkflowAction
{
    public WorkflowActionDefinition Definition { get; } = new(
        "crash.analyze-dump",
        "crash",
        "Analyze latest crash dump",
        "Run cdb -z <dump> -c \"!analyze -v;q\" against the most recent dump (or a dump path you provide) and capture the analysis output.",
        [new WorkflowActionParameter(
            "dumpPath",
            "Path to a specific .dmp file to analyze. Defaults to the most recent dump.",
            Required: false)]);

    private string DumpDirectory => dumpDirectory ?? PostmortemDebuggerConfigurator.DefaultDumpDirectory;

    public async Task<WorkflowActionResult> ExecuteAsync(
        IReadOnlyDictionary<string, string> parameters,
        CancellationToken cancellationToken = default)
    {
        var dumpPath = ResolveDumpPath(parameters);
        if (dumpPath is null)
        {
            return WorkflowActionResult.Failure(
                "No crash dump available to analyze.",
                $"No .dmp files were found in '{DumpDirectory}' and no 'dumpPath' parameter was provided.");
        }

        var result = await commandRunner.RunAsync(
                "cdb",
                ["-z", dumpPath, "-c", "!analyze -v;q"],
                cancellationToken)
            .ConfigureAwait(false);

        var artifacts = new List<string>();
        if (parameters.TryGetValue("RunDirectory", out var runDirectory) && !string.IsNullOrWhiteSpace(runDirectory))
        {
            var reportPath = Path.Combine(runDirectory, "analysis.txt");
            await File.WriteAllTextAsync(reportPath, result.StandardOutput, cancellationToken)
                .ConfigureAwait(false);
            artifacts.Add(reportPath);
        }

        var data = new Dictionary<string, string>
        {
            ["dumpPath"] = dumpPath,
            ["exitCode"] = result.ExitCode.ToString()
        };

        return new WorkflowActionResult(
            result.ExitCode == 0,
            result.ExitCode == 0
                ? $"Analyzed {Path.GetFileName(dumpPath)}."
                : $"cdb exited with code {result.ExitCode} while analyzing {Path.GetFileName(dumpPath)}.",
            data,
            artifacts,
            result.ExitCode == 0 ? null : result.StandardError.Trim());
    }

    private string? ResolveDumpPath(IReadOnlyDictionary<string, string> parameters)
    {
        if (parameters.TryGetValue("dumpPath", out var explicitPath) && !string.IsNullOrWhiteSpace(explicitPath))
        {
            return explicitPath;
        }

        if (!Directory.Exists(DumpDirectory))
        {
            return null;
        }

        return Directory.GetFiles(DumpDirectory, "*.dmp")
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();
    }
}
