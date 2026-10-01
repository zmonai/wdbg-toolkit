using WdbgToolkit.PackageManagement;

namespace WdbgToolkit.Core.Tests;

public sealed class ToolInstallerTests
{
    [Fact]
    public void CatalogHasPackageIdsForBothManagers()
    {
        Assert.All(
            ToolPackageCatalog.All,
            package =>
            {
                Assert.True(
                    !string.IsNullOrWhiteSpace(package.WinGetPackageId) ||
                    !string.IsNullOrWhiteSpace(package.ChocolateyPackageId));
            });
    }

    [Fact]
    public void CatalogContainsSysinternalsSuiteAndWinDbgPrerequisites()
    {
        var sysinternals = ToolPackageCatalog.GetById("sysinternals-suite");
        var windbg = ToolPackageCatalog.GetById("windbg");

        Assert.Equal("Microsoft.Sysinternals.Suite", sysinternals.WinGetPackageId);
        Assert.Equal("sysinternals", sysinternals.ChocolateyPackageId);
        Assert.Equal("Microsoft.WinDbg", windbg.WinGetPackageId);
        Assert.Equal("windbg", windbg.ChocolateyPackageId);
    }

    [Fact]
    public void NpcapIsAvailableThroughChocolateyButNotWinGet()
    {
        var npcap = ToolPackageCatalog.GetById("npcap");

        Assert.Null(npcap.WinGetPackageId);
        Assert.Equal("npcap", npcap.ChocolateyPackageId);
    }

    [Fact]
    public void ProcDumpIsAvailableThroughChocolateyButNotWinGet()
    {
        var procdump = ToolPackageCatalog.GetById("procdump");

        Assert.Null(procdump.WinGetPackageId);
        Assert.Equal("procdump", procdump.ChocolateyPackageId);
    }

    [Fact]
    public async Task InstallRequiresExplicitConfirmation()
    {
        var commandRunner = new FakeCommandRunner();
        var installer = new ToolInstaller(commandRunner);

        var result = await installer.InstallAsync(
            "wireshark",
            PackageManagerKind.WinGet,
            userConfirmed: false);

        Assert.Equal(InstallationStatus.ApprovalRequired, result.Status);
        Assert.Empty(commandRunner.Calls);
    }

    [Fact]
    public async Task WinGetInstallUsesExactCatalogPackageAndNonInteractiveArguments()
    {
        var commandRunner = new FakeCommandRunner();
        commandRunner.Enqueue(new CommandResult(0, "1.8.1911", string.Empty));
        commandRunner.Enqueue(new CommandResult(0, "Installed", string.Empty));
        var installer = new ToolInstaller(commandRunner);

        var result = await installer.InstallAsync(
            "wireshark",
            PackageManagerKind.WinGet,
            userConfirmed: true);

        Assert.Equal(InstallationStatus.Succeeded, result.Status);
        Assert.Collection(
            commandRunner.Calls,
            call =>
            {
                Assert.Equal("winget", call.Executable);
                Assert.Equal(["--version"], call.Arguments);
            },
            call =>
            {
                Assert.Equal("winget", call.Executable);
                Assert.Equal(
                    [
                        "install",
                        "--id",
                        "WiresharkFoundation.Wireshark",
                        "--exact",
                        "--source",
                        "winget",
                        "--accept-source-agreements",
                        "--accept-package-agreements",
                        "--disable-interactivity"
                    ],
                    call.Arguments);
            });
    }

    [Fact]
    public async Task ChocolateyInstallUsesCatalogIdAndYesFlag()
    {
        var commandRunner = new FakeCommandRunner();
        commandRunner.Enqueue(new CommandResult(0, "2.4.1", string.Empty));
        commandRunner.Enqueue(new CommandResult(0, "Installed", string.Empty));
        var installer = new ToolInstaller(commandRunner);

        var result = await installer.InstallAsync(
            "windbg",
            PackageManagerKind.Chocolatey,
            userConfirmed: true);

        Assert.Equal(InstallationStatus.Succeeded, result.Status);
        Assert.Equal(
            ["install", "windbg", "--yes", "--no-progress"],
            commandRunner.Calls[1].Arguments);
    }

    [Fact]
    public async Task MissingPackageManagerDoesNotAttemptInstallation()
    {
        var commandRunner = new FakeCommandRunner();
        commandRunner.Enqueue(new CommandResult(1, string.Empty, "not found"));
        var installer = new ToolInstaller(commandRunner);

        var result = await installer.InstallAsync(
            "wireshark",
            PackageManagerKind.Chocolatey,
            userConfirmed: true);

        Assert.Equal(InstallationStatus.ManagerUnavailable, result.Status);
        Assert.Contains("not found", result.Error);
        Assert.Single(commandRunner.Calls);
    }

    [Fact]
    public async Task NonzeroInstallExitCodeIsReportedAsFailure()
    {
        var commandRunner = new FakeCommandRunner();
        commandRunner.Enqueue(new CommandResult(0, "2.4.1", string.Empty));
        commandRunner.Enqueue(new CommandResult(5, string.Empty, "Access denied"));
        var installer = new ToolInstaller(commandRunner);

        var result = await installer.InstallAsync(
            "windbg",
            PackageManagerKind.Chocolatey,
            userConfirmed: true);

        Assert.Equal(InstallationStatus.Failed, result.Status);
        Assert.Equal(5, result.ExitCode);
        Assert.Contains("Access denied", result.Error);
    }

    private sealed class FakeCommandRunner : ICommandRunner
    {
        private readonly Queue<CommandResult> _results = new();

        public List<CommandCall> Calls { get; } = [];

        public void Enqueue(CommandResult result) => _results.Enqueue(result);

        public Task<CommandResult> RunAsync(
            string executable,
            IReadOnlyList<string> arguments,
            CancellationToken cancellationToken = default)
        {
            Calls.Add(new CommandCall(executable, arguments.ToArray()));
            return Task.FromResult(_results.Dequeue());
        }
    }

    private sealed record CommandCall(string Executable, IReadOnlyList<string> Arguments);
}
