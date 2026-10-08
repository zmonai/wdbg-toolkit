using System.ComponentModel;
using System.Diagnostics;

namespace WdbgToolkit.PackageManagement;

public sealed class SystemCommandRunner : ICommandRunner
{
    public async Task<CommandResult> RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executable);
        ArgumentNullException.ThrowIfNull(arguments);
        cancellationToken.ThrowIfCancellationRequested();

        var startInfo = new ProcessStartInfo
        {
            FileName = ResolveExecutable(executable),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
        {
            throw new InvalidOperationException($"Could not start process '{executable}'.");
        }

        var standardOutputTask = process.StandardOutput.ReadToEndAsync();
        var standardErrorTask = process.StandardError.ReadToEndAsync();

        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (InvalidOperationException)
            {
            }
            catch (Win32Exception) when (process.HasExited)
            {
            }

            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            await Task.WhenAll(standardOutputTask, standardErrorTask).ConfigureAwait(false);
            throw;
        }

        return new CommandResult(
            process.ExitCode,
            await standardOutputTask.ConfigureAwait(false),
            await standardErrorTask.ConfigureAwait(false));
    }

    private static string ResolveExecutable(string executable)
    {
        if (!string.Equals(executable, "choco", StringComparison.OrdinalIgnoreCase))
        {
            return executable;
        }

        // An installation updates the machine PATH, not the already-running app's PATH.
        var installDirectory = Environment.GetEnvironmentVariable("ChocolateyInstall") ??
                               Environment.GetEnvironmentVariable("ChocolateyInstall", EnvironmentVariableTarget.Machine);
        var directories = new[]
        {
            installDirectory,
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "chocolatey")
        };
        foreach (var directory in directories)
        {
            if (!string.IsNullOrWhiteSpace(directory))
            {
                var candidate = Path.Combine(directory, "bin", "choco.exe");
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return executable;
    }
}
