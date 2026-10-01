using WdbgToolkit.PackageManagement;

namespace WdbgToolkit.Workflows.Actions;

/// <summary>
/// Shared location for user-supplied PowerShell scripts used by the custom-logs scenario.
/// </summary>
public static class CustomScriptLocations
{
    public static string DefaultScriptsDirectory { get; } =
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "WdbgToolkit",
            "Scripts");
}

/// <summary>
/// Lists the PowerShell scripts available in the custom scripts folder, creating the
/// folder if it does not exist yet.
/// </summary>
public sealed class ListCustomScriptsAction(string? scriptsDirectory = null) : IWorkflowAction
{
    public WorkflowActionDefinition Definition { get; } = new(
        "custom-logs.list-scripts",
        "custom-logs",
        "List custom scripts",
        "List PowerShell scripts available in the custom scripts folder.",
        []);

    private string ScriptsDirectory => scriptsDirectory ?? CustomScriptLocations.DefaultScriptsDirectory;

    public Task<WorkflowActionResult> ExecuteAsync(
        IReadOnlyDictionary<string, string> parameters,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(ScriptsDirectory);
        var scripts = Directory.GetFiles(ScriptsDirectory, "*.ps1")
            .Select(Path.GetFileName)
            .OfType<string>()
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var data = new Dictionary<string, string>
        {
            ["count"] = scripts.Length.ToString(),
            ["scripts"] = string.Join(", ", scripts)
        };

        return Task.FromResult(new WorkflowActionResult(
            true,
            scripts.Length == 0
                ? $"No scripts found in {ScriptsDirectory}. Add .ps1 files there to use custom log analysis."
                : $"Found {scripts.Length} script(s) in {ScriptsDirectory}.",
            data,
            []));
    }
}

/// <summary>
/// Runs a PowerShell script from the custom scripts folder via
/// <c>powershell.exe -NoProfile -ExecutionPolicy Bypass -File &lt;script&gt;</c> and captures
/// its output as a text artifact.
/// </summary>
public sealed class RunCustomScriptAction(ICommandRunner commandRunner, string? scriptsDirectory = null)
    : IWorkflowAction
{
    public WorkflowActionDefinition Definition { get; } = new(
        "custom-logs.run-script",
        "custom-logs",
        "Run custom script",
        "Run a PowerShell script from the custom scripts folder via powershell.exe -NoProfile -ExecutionPolicy Bypass -File <script> and capture its output.",
        [
            new WorkflowActionParameter(
                "scriptPath",
                "Path to the .ps1 script to run (absolute, or a file name inside the custom scripts folder).",
                Required: true),
            new WorkflowActionParameter(
                "arguments",
                "Additional arguments passed to the script, space-separated.",
                Required: false,
                DefaultValue: string.Empty)
        ]);

    private string ScriptsDirectory => scriptsDirectory ?? CustomScriptLocations.DefaultScriptsDirectory;

    public async Task<WorkflowActionResult> ExecuteAsync(
        IReadOnlyDictionary<string, string> parameters,
        CancellationToken cancellationToken = default)
    {
        if (!parameters.TryGetValue("scriptPath", out var scriptPath) || string.IsNullOrWhiteSpace(scriptPath))
        {
            return WorkflowActionResult.Failure(
                "No script was provided.",
                "The 'scriptPath' parameter is required.");
        }

        var resolvedScriptPath = Path.IsPathRooted(scriptPath)
            ? scriptPath
            : Path.Combine(ScriptsDirectory, scriptPath);
        if (!File.Exists(resolvedScriptPath))
        {
            return WorkflowActionResult.Failure(
                $"Script not found: {resolvedScriptPath}",
                $"No file exists at '{resolvedScriptPath}'.");
        }

        var arguments = new List<string> { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", resolvedScriptPath };
        if (parameters.TryGetValue("arguments", out var extraArguments) && !string.IsNullOrWhiteSpace(extraArguments))
        {
            arguments.AddRange(extraArguments.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        }

        var result = await commandRunner.RunAsync("powershell", arguments, cancellationToken)
            .ConfigureAwait(false);

        var artifacts = new List<string>();
        if (parameters.TryGetValue("RunDirectory", out var runDirectory) && !string.IsNullOrWhiteSpace(runDirectory))
        {
            var outputPath = Path.Combine(runDirectory, "output.log");
            await File.WriteAllTextAsync(
                    outputPath,
                    result.StandardOutput + Environment.NewLine + result.StandardError,
                    cancellationToken)
                .ConfigureAwait(false);
            artifacts.Add(outputPath);
        }

        return new WorkflowActionResult(
            result.ExitCode == 0,
            result.ExitCode == 0
                ? $"Ran {Path.GetFileName(resolvedScriptPath)}."
                : $"{Path.GetFileName(resolvedScriptPath)} exited with code {result.ExitCode}.",
            new Dictionary<string, string>
            {
                ["scriptPath"] = resolvedScriptPath,
                ["exitCode"] = result.ExitCode.ToString()
            },
            artifacts,
            result.ExitCode == 0 ? null : result.StandardError.Trim());
    }
}
