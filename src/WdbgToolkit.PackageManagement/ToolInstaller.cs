using System.ComponentModel;

namespace WdbgToolkit.PackageManagement;

public sealed record PackageManagerAvailability(
    PackageManagerKind Manager,
    bool IsAvailable,
    string? Version,
    string? Error);

public enum InstallationStatus
{
    Succeeded,
    Failed,
    ManagerUnavailable,
    ApprovalRequired
}

public sealed record ToolInstallationResult(
    string ToolId,
    PackageManagerKind Manager,
    InstallationStatus Status,
    int? ExitCode,
    string StandardOutput,
    string StandardError,
    string? Error);

public sealed class ToolInstaller(ICommandRunner commandRunner)
{
    public async Task<PackageManagerAvailability> CheckAvailabilityAsync(
        PackageManagerKind manager,
        CancellationToken cancellationToken = default)
    {
        var executable = ExecutableFor(manager);
        try
        {
            var result = await commandRunner.RunAsync(
                    executable,
                    ["--version"],
                    cancellationToken)
                .ConfigureAwait(false);

            var version = result.StandardOutput.Trim();
            if (result.ExitCode == 0)
            {
                return new PackageManagerAvailability(
                    manager,
                    true,
                    version,
                    null);
            }

            return new PackageManagerAvailability(
                manager,
                false,
                null,
                FailureMessage(result));
        }
        catch (Win32Exception exception)
        {
            return new PackageManagerAvailability(
                manager,
                false,
                null,
                $"{executable} is unavailable: {exception.Message}");
        }
        catch (FileNotFoundException exception)
        {
            return new PackageManagerAvailability(
                manager,
                false,
                null,
                $"{executable} is unavailable: {exception.Message}");
        }
    }

    public async Task<ToolInstallationResult> InstallAsync(
        string toolId,
        PackageManagerKind manager,
        bool userConfirmed,
        CancellationToken cancellationToken = default)
    {
        var package = ToolPackageCatalog.GetById(toolId);
        var packageId = PackageIdFor(package, manager);
        if (packageId is null)
        {
            throw new NotSupportedException(
                $"The {manager} package identifier for '{package.Name}' is not configured.");
        }

        if (!userConfirmed)
        {
            return new ToolInstallationResult(
                package.Id,
                manager,
                InstallationStatus.ApprovalRequired,
                null,
                string.Empty,
                string.Empty,
                "Installation was not started because explicit user confirmation was not provided.");
        }

        var availability = await CheckAvailabilityAsync(manager, cancellationToken)
            .ConfigureAwait(false);
        if (!availability.IsAvailable)
        {
            return new ToolInstallationResult(
                package.Id,
                manager,
                InstallationStatus.ManagerUnavailable,
                null,
                string.Empty,
                string.Empty,
                availability.Error);
        }

        try
        {
            var result = await commandRunner.RunAsync(
                    ExecutableFor(manager),
                    InstallArguments(manager, packageId),
                    cancellationToken)
                .ConfigureAwait(false);

            return new ToolInstallationResult(
                package.Id,
                manager,
                result.ExitCode == 0 ? InstallationStatus.Succeeded : InstallationStatus.Failed,
                result.ExitCode,
                result.StandardOutput,
                result.StandardError,
                result.ExitCode == 0 ? null : FailureMessage(result));
        }
        catch (Win32Exception exception)
        {
            return new ToolInstallationResult(
                package.Id,
                manager,
                InstallationStatus.ManagerUnavailable,
                null,
                string.Empty,
                string.Empty,
                $"{ExecutableFor(manager)} could not be started: {exception.Message}");
        }
        catch (FileNotFoundException exception)
        {
            return new ToolInstallationResult(
                package.Id,
                manager,
                InstallationStatus.ManagerUnavailable,
                null,
                string.Empty,
                string.Empty,
                $"{ExecutableFor(manager)} could not be started: {exception.Message}");
        }
    }

    private static string ExecutableFor(PackageManagerKind manager) =>
        manager switch
        {
            PackageManagerKind.WinGet => "winget",
            PackageManagerKind.Chocolatey => "choco",
            _ => throw new ArgumentOutOfRangeException(nameof(manager), manager, "Unknown package manager.")
        };

    private static string? PackageIdFor(ToolPackage package, PackageManagerKind manager) =>
        manager switch
        {
            PackageManagerKind.WinGet => package.WinGetPackageId,
            PackageManagerKind.Chocolatey => package.ChocolateyPackageId,
            _ => throw new ArgumentOutOfRangeException(nameof(manager), manager, "Unknown package manager.")
        };

    private static IReadOnlyList<string> InstallArguments(PackageManagerKind manager, string packageId) =>
        manager switch
        {
            PackageManagerKind.WinGet =>
            [
                "install",
                "--id",
                packageId,
                "--exact",
                "--source",
                "winget",
                "--accept-source-agreements",
                "--accept-package-agreements",
                "--disable-interactivity"
            ],
            PackageManagerKind.Chocolatey =>
            [
                "install",
                packageId,
                "--yes",
                "--no-progress"
            ],
            _ => throw new ArgumentOutOfRangeException(nameof(manager), manager, "Unknown package manager.")
        };

    private static string FailureMessage(CommandResult result)
    {
        var detail = string.Join(
            Environment.NewLine,
            new[] { result.StandardError.Trim(), result.StandardOutput.Trim() }
                .Where(value => !string.IsNullOrWhiteSpace(value)));

        return string.IsNullOrWhiteSpace(detail)
            ? $"Package-manager command failed with exit code {result.ExitCode}."
            : $"Package-manager command failed with exit code {result.ExitCode}: {detail}";
    }
}
