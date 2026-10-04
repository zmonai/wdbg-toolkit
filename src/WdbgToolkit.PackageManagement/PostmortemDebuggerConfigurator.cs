namespace WdbgToolkit.PackageManagement;

public sealed record PostmortemDebuggerConfigurationResult(
    bool Succeeded,
    string DumpDirectory,
    int? ExitCode,
    string StandardOutput,
    string StandardError,
    string? Error);

public sealed class PostmortemDebuggerConfigurator(
    ICommandRunner commandRunner,
    string? dumpDirectory = null)
{
    public static string DefaultDumpDirectory { get; } =
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "WdbgToolkit",
            "CrashDumps");

    private string DumpDirectory => dumpDirectory ?? DefaultDumpDirectory;

    public async Task<PostmortemDebuggerConfigurationResult> ConfigureProcDumpAsync(
        bool userConfirmed,
        CancellationToken cancellationToken = default)
    {
        if (!userConfirmed)
        {
            return new PostmortemDebuggerConfigurationResult(
                false,
                DumpDirectory,
                null,
                string.Empty,
                string.Empty,
                "Postmortem debugger configuration was not changed because explicit user confirmation was not provided.");
        }

        Directory.CreateDirectory(DumpDirectory);

        var result = await commandRunner.RunAsync(
                "procdump",
                ["-accepteula", "-i", DumpDirectory, "-ma"],
                cancellationToken)
            .ConfigureAwait(false);

        return new PostmortemDebuggerConfigurationResult(
            result.ExitCode == 0,
            DumpDirectory,
            result.ExitCode,
            result.StandardOutput,
            result.StandardError,
            result.ExitCode == 0
                ? null
                : string.Join(
                    Environment.NewLine,
                    new[] { result.StandardError.Trim(), result.StandardOutput.Trim() }
                        .Where(value => !string.IsNullOrWhiteSpace(value))));
    }
}
